using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Beam.Services
{
    public static class CalculatorService
    {
        private static readonly DataTable Table = new();

        // Approximate exchange rates relative to 1 USD
        private static readonly Dictionary<string, double> UsdRates = new(StringComparer.OrdinalIgnoreCase)
        {
            ["USD"] = 1.0,
            ["INR"] = 84.40,
            ["EUR"] = 0.92,
            ["GBP"] = 0.78,
            ["JPY"] = 153.50,
            ["CAD"] = 1.39,
            ["AUD"] = 1.52,
            ["CHF"] = 0.88,
            ["CNY"] = 7.24,
            ["AED"] = 3.67,
            ["SGD"] = 1.34
        };

        public static (bool IsValid, string Expression, string Result) Evaluate(string input)
        {
            if (string.IsNullOrWhiteSpace(input) || input.Length < 2)
                return (false, string.Empty, string.Empty);

            var clean = input.Trim();

            // 1. World Timezone Queries: e.g. "time in tokyo", "time in london", "time in india"
            var timeMatch = Regex.Match(clean, @"^time\s+in\s+([a-zA-Z\s]+)$", RegexOptions.IgnoreCase);
            if (timeMatch.Success)
            {
                var city = timeMatch.Groups[1].Value.Trim().ToLowerInvariant();
                var (found, zoneName, offsetHours) = LookupTimezone(city);
                if (found)
                {
                    var targetTime = DateTime.UtcNow.AddHours(offsetHours);
                    return (true, $"Current time in {zoneName}", $"{targetTime:hh:mm tt} ({targetTime:ddd, MMM d})");
                }
            }

            // 2. Currency Conversion: e.g. "100 usd to inr" or "5000 inr in usd"
            var currMatch = Regex.Match(clean, @"^([\d.,]+)\s*([a-zA-Z]{3})\s+(?:to|in)\s+([a-zA-Z]{3})$", RegexOptions.IgnoreCase);
            if (currMatch.Success)
            {
                var amtStr = currMatch.Groups[1].Value.Replace(",", "");
                var fromCurr = currMatch.Groups[2].Value.ToUpperInvariant();
                var toCurr = currMatch.Groups[3].Value.ToUpperInvariant();

                if (double.TryParse(amtStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var amt) &&
                    UsdRates.TryGetValue(fromCurr, out var fromRate) &&
                    UsdRates.TryGetValue(toCurr, out var toRate))
                {
                    double inUsd = amt / fromRate;
                    double converted = inUsd * toRate;
                    return (true, $"{amt:N2} {fromCurr} to {toCurr}", $"{converted:N2} {toCurr}");
                }
            }

            // 3. Temperature Conversion: e.g. "100 c to f", "72 f in c"
            var tempMatch = Regex.Match(clean, @"^(-?[\d.]+)\s*(c|celsius|f|fahrenheit|k|kelvin)\s+(?:to|in)\s+(c|celsius|f|fahrenheit|k|kelvin)$", RegexOptions.IgnoreCase);
            if (tempMatch.Success && double.TryParse(tempMatch.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var tVal))
            {
                string u1 = tempMatch.Groups[2].Value[..1].ToUpperInvariant();
                string u2 = tempMatch.Groups[3].Value[..1].ToUpperInvariant();
                double celsius = u1 switch
                {
                    "F" => (tVal - 32.0) * 5.0 / 9.0,
                    "K" => tVal - 273.15,
                    _ => tVal
                };
                double res = u2 switch
                {
                    "F" => (celsius * 9.0 / 5.0) + 32.0,
                    "K" => celsius + 273.15,
                    _ => celsius
                };
                return (true, $"{tVal}°{u1} to °{u2}", $"{FormatNumber(res)} °{u2}");
            }

            // 4. Unit Conversions (Length, Weight, Data)
            var unitMatch = Regex.Match(clean, @"^([\d.]+)\s*([a-zA-Z]+)\s+(?:to|in)\s+([a-zA-Z]+)$", RegexOptions.IgnoreCase);
            if (unitMatch.Success && double.TryParse(unitMatch.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var uVal))
            {
                string fromU = unitMatch.Groups[2].Value.ToLowerInvariant();
                string toU = unitMatch.Groups[3].Value.ToLowerInvariant();
                if (TryConvertUnit(uVal, fromU, toU, out var uRes, out var label))
                {
                    return (true, clean, $"{FormatNumber(uRes)} {label}");
                }
            }

            // 5. Base Conversions: "255 in hex", "255 in bin", "0xff in dec"
            var hexMatch = Regex.Match(clean, @"^(\d+)\s+(?:to|in)\s+(hex|bin|binary)$", RegexOptions.IgnoreCase);
            if (hexMatch.Success && long.TryParse(hexMatch.Groups[1].Value, out var intVal))
            {
                var mode = hexMatch.Groups[2].Value.ToLowerInvariant();
                if (mode == "hex") return (true, clean, $"0x{intVal:X}");
                return (true, clean, Convert.ToString(intVal, 2));
            }

            // 6. Percentage check: e.g. "20% of 850"
            var pctOfMatch = Regex.Match(clean, @"^([\d.]+)\s*%\s*(?:of|on|\*)\s*([\d.]+)$", RegexOptions.IgnoreCase);
            if (pctOfMatch.Success)
            {
                if (double.TryParse(pctOfMatch.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var pct) &&
                    double.TryParse(pctOfMatch.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var total))
                {
                    return (true, clean, FormatNumber((pct / 100.0) * total));
                }
            }

            // 7. Percentage addition/subtraction: "1500 + 18%" or "1500 - 10%"
            var pctAddSubMatch = Regex.Match(clean, @"^([\d.]+)\s*([+-])\s*([\d.]+)\s*%$", RegexOptions.IgnoreCase);
            if (pctAddSubMatch.Success)
            {
                if (double.TryParse(pctAddSubMatch.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var baseVal) &&
                    double.TryParse(pctAddSubMatch.Groups[3].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var pctVal))
                {
                    var op = pctAddSubMatch.Groups[2].Value;
                    var res = op == "+"
                        ? baseVal * (1.0 + pctVal / 100.0)
                        : baseVal * (1.0 - pctVal / 100.0);
                    return (true, clean, FormatNumber(res));
                }
            }

            // 8. Ratio as percentage: "50 as % of 200"
            var asPctMatch = Regex.Match(clean, @"^([\d.]+)\s*(?:as\s*(?:a\s*)?%?\s*of)\s*([\d.]+)$", RegexOptions.IgnoreCase);
            if (asPctMatch.Success)
            {
                if (double.TryParse(asPctMatch.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var valA) &&
                    double.TryParse(asPctMatch.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var valB) &&
                    valB != 0)
                {
                    return (true, clean, $"{FormatNumber((valA / valB) * 100.0)}%");
                }
            }

            // 9. Power exponent: e.g. "2^10"
            var powMatch = Regex.Match(clean, @"^([\d.]+)\s*\^\s*([\d.]+)$");
            if (powMatch.Success &&
                double.TryParse(powMatch.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var b) &&
                double.TryParse(powMatch.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var exp))
            {
                return (true, clean, FormatNumber(Math.Pow(b, exp)));
            }

            // 10. Math functions: sqrt, sin, cos, tan, abs, log
            var fnMatch = Regex.Match(clean, @"^(sqrt|sin|cos|tan|abs|log)\s*\(\s*(-?[\d.]+)\s*\)$", RegexOptions.IgnoreCase);
            if (fnMatch.Success)
            {
                var fn = fnMatch.Groups[1].Value.ToLowerInvariant();
                if (double.TryParse(fnMatch.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var arg))
                {
                    double res = fn switch
                    {
                        "sqrt" => Math.Sqrt(arg),
                        "sin" => Math.Sin(arg * Math.PI / 180.0),
                        "cos" => Math.Cos(arg * Math.PI / 180.0),
                        "tan" => Math.Tan(arg * Math.PI / 180.0),
                        "abs" => Math.Abs(arg),
                        "log" => Math.Log10(arg),
                        _ => 0
                    };
                    return (true, clean, FormatNumber(res));
                }
            }

            // 11. Standard arithmetic expressions
            if (!Regex.IsMatch(clean, @"[\d]") || !Regex.IsMatch(clean, @"[+\-*/%]"))
                return (false, string.Empty, string.Empty);

            var expr = clean.Replace(" ", "");
            if (!Regex.IsMatch(expr, @"^[\d+\-*/().%]+$") || Regex.IsMatch(expr, @"[+\-*/]$"))
                return (false, string.Empty, string.Empty);

            try
            {
                var val = Table.Compute(expr, null);
                if (val != null && val != DBNull.Value)
                {
                    return (true, clean, FormatNumber(Convert.ToDouble(val)));
                }
            }
            catch { }

            return (false, string.Empty, string.Empty);
        }

        private static bool TryConvertUnit(double val, string from, string to, out double result, out string unitLabel)
        {
            result = 0;
            unitLabel = to;

            // Length in meters
            var lengthFactors = new Dictionary<string, double>
            {
                ["mm"] = 0.001, ["cm"] = 0.01, ["m"] = 1.0, ["km"] = 1000.0,
                ["in"] = 0.0254, ["inch"] = 0.0254, ["inches"] = 0.0254,
                ["ft"] = 0.3048, ["feet"] = 0.3048, ["foot"] = 0.3048,
                ["yd"] = 0.9144, ["yard"] = 0.9144, ["yards"] = 0.9144,
                ["mi"] = 1609.344, ["mile"] = 1609.344, ["miles"] = 1609.344
            };
            if (lengthFactors.TryGetValue(from, out var lf1) && lengthFactors.TryGetValue(to, out var lf2))
            {
                result = (val * lf1) / lf2;
                return true;
            }

            // Weight in grams
            var weightFactors = new Dictionary<string, double>
            {
                ["mg"] = 0.001, ["g"] = 1.0, ["kg"] = 1000.0,
                ["oz"] = 28.3495, ["lb"] = 453.592, ["lbs"] = 453.592, ["pound"] = 453.592, ["pounds"] = 453.592
            };
            if (weightFactors.TryGetValue(from, out var wf1) && weightFactors.TryGetValue(to, out var wf2))
            {
                result = (val * wf1) / wf2;
                return true;
            }

            // Data in bytes
            var dataFactors = new Dictionary<string, double>
            {
                ["b"] = 1.0, ["kb"] = 1024.0, ["mb"] = 1048576.0,
                ["gb"] = 1073741824.0, ["tb"] = 1099511627776.0
            };
            if (dataFactors.TryGetValue(from, out var df1) && dataFactors.TryGetValue(to, out var df2))
            {
                result = (val * df1) / df2;
                unitLabel = to.ToUpperInvariant();
                return true;
            }

            return false;
        }

        private static (bool Found, string Name, double Offset) LookupTimezone(string city)
        {
            return city switch
            {
                "tokyo" or "japan" => (true, "Tokyo (JST)", 9.0),
                "london" or "uk" => (true, "London (GMT/BST)", 0.0),
                "new york" or "nyc" or "est" => (true, "New York (EST)", -5.0),
                "sf" or "san francisco" or "la" or "pst" => (true, "San Francisco (PST)", -8.0),
                "india" or "mumbai" or "delhi" or "bangalore" or "ist" => (true, "India (IST)", 5.5),
                "dubai" or "uae" => (true, "Dubai (GST)", 4.0),
                "singapore" or "sg" => (true, "Singapore (SGT)", 8.0),
                "sydney" or "australia" => (true, "Sydney (AEDT)", 11.0),
                "berlin" or "paris" or "cet" => (true, "Central Europe (CET)", 1.0),
                "utc" or "gmt" => (true, "UTC", 0.0),
                _ => (false, "", 0)
            };
        }

        private static string FormatNumber(double val)
        {
            if (double.IsInfinity(val) || double.IsNaN(val))
                return "Error";

            if (Math.Abs(val % 1) < 1e-9)
                return val.ToString("N0", CultureInfo.InvariantCulture);

            return val.ToString("N2", CultureInfo.InvariantCulture);
        }
    }
}
