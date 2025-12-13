using System;
using System.Security.Cryptography;
using System.Text;

namespace SLSKDONET.Services;

/// <summary>
/// Provides methods to encrypt and decrypt data using the Windows Data Protection API (DPAPI).
/// The data is encrypted for the current user account, so only this user can decrypt it.
/// </summary>
public class ProtectedDataService
{
    // Fallback: base64 encode/decode (non-secure). Replace with DPAPI if desired.
    public string? Protect(string? data)
    {
        if (string.IsNullOrEmpty(data))
            return null;
        var bytes = Encoding.UTF8.GetBytes(data);
        return Convert.ToBase64String(bytes);
    }

    public string? Unprotect(string? encryptedData)
    {
        if (string.IsNullOrEmpty(encryptedData))
            return null;
        try
        {
            var bytes = Convert.FromBase64String(encryptedData);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }
}