from pathlib import Path

expected = {
    'src/ProxyToAnyConnect/Proxy/ProxyServer.cs': '8f8692442e84f779d04c282770acf6c622c0504e',
    'src/ProxyToAnyConnect/Network/L2tpDnsResolver.cs': '38f976ea90b3b76d2aa9692c2ac4822f63957e0e',
    'tests/ProxyToAnyConnect.SelfTests/CombinedTestRunner.cs': '8d071b019ec3b6e29f01cc98e500835a9ef6e8a5',
    'tests/ProxyToAnyConnect.SelfTests/ProxyConnectAuthoritySelfTests.cs': '6a77104ec6b6d0b82350a296a8a408b7e70b802f',
    'tests/ProxyToAnyConnect.SelfTests/ProxyConnectPortGrammarSelfTests.cs': 'ccca4399cbc3734c20bf997435ee361c5c399be6',
}

import subprocess
for path, sha in expected.items():
    actual = subprocess.check_output(['git', 'rev-parse', f'HEAD:{path}'], text=True).strip()
    if actual != sha:
        raise SystemExit(f'unexpected input blob for {path}: {actual}')

proxy = Path('src/ProxyToAnyConnect/Proxy/ProxyServer.cs')
data = proxy.read_bytes()

old = b'''        var separator = authority.IndexOf(':');
        if (separator >= 0 && authority.IndexOf(':', separator + 1) >= 0)
        {
            throw new NotSupportedException("IPv6 proxy targets are not supported yet.");
        }
'''
new = b'''        var separator = authority.IndexOf(':');
        if (separator >= 0 && authority.IndexOf(':', separator + 1) >= 0)
        {
            throw new InvalidDataException($"Invalid CONNECT target '{authority}'.");
        }
'''
if data.count(old) != 1:
    raise SystemExit(f'expected one unbracketed multi-colon block, got {data.count(old)}')
data = data.replace(old, new)

old = b'''        var host = separator < 0 ? authority : authority[..separator];
        var port = defaultPort;
        if (separator >= 0 &&
            (!int.TryParse(authority[(separator + 1)..], out port) || port is < 1 or > 65535))
        {
            throw new InvalidDataException($"Invalid CONNECT target '{authority}'.");
        }
'''
new = b'''        var host = separator < 0 ? authority : authority[..separator];
        var port = defaultPort;
        if (separator >= 0 && !TryParseConnectPort(authority.AsSpan(separator + 1), out port))
        {
            throw new InvalidDataException($"Invalid CONNECT target '{authority}'.");
        }
'''
if data.count(old) != 1:
    raise SystemExit(f'expected one CONNECT port parse block, got {data.count(old)}')
data = data.replace(old, new)

marker = b'''        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException($"Invalid CONNECT target '{authority}'.", ex);
        }
    }

    private static Uri ParseAbsoluteHttpUri'''
replacement = b'''        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException($"Invalid CONNECT target '{authority}'.", ex);
        }
    }

    private static bool TryParseConnectPort(ReadOnlySpan<char> value, out int port)
    {
        port = 0;
        if (value.IsEmpty)
        {
            return false;
        }

        var parsed = 0;
        foreach (var character in value)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }

            parsed = parsed * 10 + (character - '0');
            if (parsed > 65535)
            {
                return false;
            }
        }

        if (parsed == 0)
        {
            return false;
        }

        port = parsed;
        return true;
    }

    private static Uri ParseAbsoluteHttpUri'''
if data.count(marker) != 1:
    raise SystemExit(f'expected one CONNECT port helper marker, got {data.count(marker)}')
proxy.write_bytes(data.replace(marker, replacement))

runner = Path('tests/ProxyToAnyConnect.SelfTests/CombinedTestRunner.cs')
data = runner.read_bytes()
anchor = b'        await RunAsync(nameof(ProxyConnectAuthoritySelfTests), ProxyConnectAuthoritySelfTests.RunAsync);\n'
expanded = anchor + b'        await RunAsync(nameof(ProxyConnectPortGrammarSelfTests), ProxyConnectPortGrammarSelfTests.RunAsync);\n'
if data.count(anchor) != 1:
    raise SystemExit(f'expected one CONNECT authority runner anchor, got {data.count(anchor)}')
runner.write_bytes(data.replace(anchor, expanded))
