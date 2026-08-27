from pathlib import Path
import subprocess

expected = {
    'src/ProxyToAnyConnect/Proxy/ProxyServer.cs': '920e8c335bc5488dc0f08dae87e4c0247e9b78eb',
    'tests/ProxyToAnyConnect.SelfTests/CombinedTestRunner.cs': '7d1d807e7696a5527e18231814e9f7ebb2e56e79',
    'tests/ProxyToAnyConnect.SelfTests/ProxyHttpCanonicalHostSelfTests.cs': '0b5c14ad82ae7ab4c40d9328d7a53301afb73704',
    'tests/ProxyToAnyConnect.SelfTests/ProxyRoutingHostCanonicalizationSelfTests.cs': '4b8759e3c80216e60cc74cf3f60add0f63b59f34',
}
for path, sha in expected.items():
    actual = subprocess.check_output(['git', 'rev-parse', f'HEAD:{path}'], text=True).strip()
    if actual != sha:
        raise SystemExit(f'unexpected #28/#30 input blob for {path}: {actual}')


def replace_block(data: bytes, old_lf: bytes, new_lf: bytes, label: str) -> bytes:
    matches = []
    for newline in (b'\r\n', b'\n'):
        old = old_lf.replace(b'\n', newline)
        count = data.count(old)
        if count:
            matches.append((newline, old, count))
    if len(matches) != 1 or matches[0][2] != 1:
        detail = ', '.join(f'{nl!r}:{count}' for nl, _, count in matches) or 'none'
        raise SystemExit(f'expected one {label}, matches={detail}')
    newline, old, _ = matches[0]
    return data.replace(old, new_lf.replace(b'\n', newline))


proxy = Path('src/ProxyToAnyConnect/Proxy/ProxyServer.cs')
data = proxy.read_bytes()
data = replace_block(
    data,
    b'''        var uri = ParseAbsoluteHttpUri(request.Target);
        var host = uri.IdnHost;
        var port = uri.IsDefaultPort ? 80 : uri.Port;
        var pathAndQuery = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
        var authority = BuildHttpHostAuthority(uri);

        await using var upstream = await _socketFactory.ConnectAsync(host, port, cancellationToken);
''',
    b'''        var (host, port, authority, pathAndQuery) = ParseHttpTarget(request.Target);

        await using var upstream = await _socketFactory.ConnectAsync(host, port, cancellationToken);
''',
    'plain-HTTP unified target contract')

data = replace_block(
    data,
    b'''        if (host.Length == 0)
        {
            throw new InvalidDataException($"Invalid CONNECT target '{authority}'.");
        }

        foreach (var character in host)
        {
            if (character <= 0x20 || character == 0x7F ||
                character == (char)0x5C ||
                character is '@' or '/' or '?' or '#' or ':')
            {
                throw new InvalidDataException($"Invalid CONNECT target '{authority}'.");
            }
        }

        if (IPAddress.TryParse(host, out var literal))
        {
            if (literal.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new NotSupportedException("IPv6 proxy targets are not supported yet.");
            }

            return (literal.ToString(), port);
        }

        try
        {
            return (L2tpDnsResolver.NormalizeDnsHostStrict(host), port);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException($"Invalid CONNECT target '{authority}'.", ex);
        }
''',
    b'''        try
        {
            return (NormalizeRoutingHost(host), port);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"Invalid CONNECT target '{authority}'.", ex);
        }
''',
    'CONNECT routing-host normalization')

normalizer = b'''    internal static string NormalizeRoutingHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidDataException("Routing host is empty.");
        }

        if (host.StartsWith("[", StringComparison.Ordinal) || host.Contains(':'))
        {
            throw new NotSupportedException("IPv6 proxy targets are not supported yet.");
        }

        foreach (var character in host)
        {
            if (character <= 0x20 || character == 0x7F ||
                character == (char)0x5C ||
                character is '@' or '/' or '?' or '#')
            {
                throw new InvalidDataException($"Invalid routing host '{host}'.");
            }
        }

        if (host.EndsWith(".", StringComparison.Ordinal) && host.Length > 1)
        {
            var withoutRoot = host[..^1];
            if (IPAddress.TryParse(withoutRoot, out var rootedLiteral) &&
                rootedLiteral.AddressFamily == AddressFamily.InterNetwork)
            {
                throw new InvalidDataException(
                    $"IPv4 routing host '{host}' must use canonical dotted-decimal form without a DNS root dot.");
            }
        }

        if (IPAddress.TryParse(host, out var literal))
        {
            if (literal.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new NotSupportedException("IPv6 proxy targets are not supported yet.");
            }

            var canonical = literal.ToString();
            if (!host.Equals(canonical, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"IPv4 routing host '{host}' is not canonical dotted-decimal form.");
            }

            return canonical;
        }

        try
        {
            return L2tpDnsResolver.NormalizeDnsHostStrict(host);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException($"Invalid routing host '{host}'.", ex);
        }
    }

'''
marker = b'    private static Uri ParseAbsoluteHttpUri(string target)\n'
data = replace_block(data, marker, normalizer + marker, 'routing-host normalizer insertion')

parse_http_target = b'''    internal static (string Host, int Port, string Authority, string PathAndQuery) ParseHttpTarget(string target)
    {
        const string HttpPrefix = "http://";
        if (string.IsNullOrEmpty(target) ||
            !target.StartsWith(HttpPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Plain HTTP proxy requests must use an absolute http:// URI.");
        }

        var authorityStart = HttpPrefix.Length;
        var authorityEnd = target.Length;
        for (var index = authorityStart; index < target.Length; index++)
        {
            if (target[index] is '/' or '?' or '#')
            {
                authorityEnd = index;
                break;
            }
        }

        var rawAuthority = target.AsSpan(authorityStart, authorityEnd - authorityStart);
        if (rawAuthority.IsEmpty || rawAuthority.IndexOf('@') >= 0)
        {
            throw new InvalidDataException("Plain HTTP proxy requests must not contain userinfo or an empty authority.");
        }

        if (rawAuthority[0] == '[')
        {
            throw new NotSupportedException("IPv6 proxy targets are not supported yet.");
        }

        var separator = rawAuthority.IndexOf(':');
        if (separator >= 0 && rawAuthority[(separator + 1)..].IndexOf(':') >= 0)
        {
            throw new InvalidDataException("Plain HTTP target contains an invalid multi-colon authority.");
        }

        var rawHostSpan = separator < 0 ? rawAuthority : rawAuthority[..separator];
        if (rawHostSpan.IsEmpty)
        {
            throw new InvalidDataException("Plain HTTP target host is empty.");
        }

        var rawPort = 80;
        if (separator >= 0 && !TryParseConnectPort(rawAuthority[(separator + 1)..], out rawPort))
        {
            throw new InvalidDataException("Plain HTTP target port must be ASCII decimal in the range 1..65535.");
        }

        var canonicalRawHost = NormalizeRoutingHost(rawHostSpan.ToString());
        var uri = ParseAbsoluteHttpUri(target);
        if (uri.HostNameType == UriHostNameType.IPv6)
        {
            throw new NotSupportedException("IPv6 proxy targets are not supported yet.");
        }

        var canonicalUriHost = NormalizeRoutingHost(uri.IdnHost);
        var uriPort = uri.IsDefaultPort ? 80 : uri.Port;
        if (!canonicalRawHost.Equals(canonicalUriHost, StringComparison.Ordinal) || rawPort != uriPort)
        {
            throw new InvalidDataException(
                "Plain HTTP target authority changed during URI parsing and is rejected before routing.");
        }

        var authority = rawPort == 80 ? canonicalRawHost : $"{canonicalRawHost}:{rawPort}";
        var pathAndQuery = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
        return (canonicalRawHost, rawPort, authority, pathAndQuery);
    }

'''
marker = b'    internal static string BuildHttpHostAuthority(Uri uri)\n'
data = replace_block(data, marker, parse_http_target + marker, 'plain-HTTP target parser insertion')

data = replace_block(
    data,
    b'''        var host = uri.IdnHost;
        return uri.IsDefaultPort ? host : $"{host}:{uri.Port}";
''',
    b'''        var host = NormalizeRoutingHost(uri.IdnHost);
        return uri.IsDefaultPort ? host : $"{host}:{uri.Port}";
''',
    'generated Host canonicalization')
proxy.write_bytes(data)

runner = Path('tests/ProxyToAnyConnect.SelfTests/CombinedTestRunner.cs')
data = runner.read_bytes()
data = replace_block(
    data,
    b'''        await RunAsync(nameof(ProxyHttpHostAuthoritySelfTests), ProxyHttpHostAuthoritySelfTests.RunAsync);
        await RunAsync(nameof(ProxyHttpHeaderValueSelfTests), ProxyHttpHeaderValueSelfTests.RunAsync);
''',
    b'''        await RunAsync(nameof(ProxyHttpHostAuthoritySelfTests), ProxyHttpHostAuthoritySelfTests.RunAsync);
        await RunAsync(nameof(ProxyHttpCanonicalHostSelfTests), ProxyHttpCanonicalHostSelfTests.RunAsync);
        await RunAsync(nameof(ProxyRoutingHostCanonicalizationSelfTests), ProxyRoutingHostCanonicalizationSelfTests.RunAsync);
        await RunAsync(nameof(ProxyHttpHeaderValueSelfTests), ProxyHttpHeaderValueSelfTests.RunAsync);
''',
    '#28/#30 runner insertion')
runner.write_bytes(data)
