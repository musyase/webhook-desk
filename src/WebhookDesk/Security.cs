using System.Security.Cryptography;
using System.Text;

namespace WebhookDesk;

public static class TokenCheck
{
    public static bool Matches(string expected, string supplied)
    {
        var left = Encoding.UTF8.GetBytes(expected);
        var right = Encoding.UTF8.GetBytes(supplied);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}

public static class ReplayTarget
{
    public static bool TryParse(string? value, out Uri? target)
    {
        target = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttp || !parsed.IsLoopback ||
            parsed.UserInfo.Length > 0 || parsed.Fragment.Length > 0) return false;
        target = parsed;
        return true;
    }
}
