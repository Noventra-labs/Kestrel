using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Beam.Services;

internal static class CloudIdentity
{
    private static readonly object Gate = new();
    internal static string CredentialDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kestrel", "CloudCredentials");

    public static Uri ValidateBackend(string backendUrl)
    {
        var uri = new Uri(backendUrl.TrimEnd('/') + "/", UriKind.Absolute);
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            throw new InvalidOperationException("Cloud connections require HTTPS (HTTP is allowed only on localhost).");
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Enter a backend URL without credentials, query, or fragment.");
        return uri;
    }

    public static string GetCredential(Uri backend)
    {
        // Each backend gets an independent credential, protected for this Windows user.
        string scope = backend.AbsoluteUri;
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
        string folder = CredentialDirectory;
        string path = Path.Combine(folder, id + ".bin");
        lock (Gate)
        {
            if (File.Exists(path))
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser));
            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            Directory.CreateDirectory(folder);
            byte[] encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser);
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Write(encrypted);
            file.Flush(true);
            return token;
        }
    }
}
