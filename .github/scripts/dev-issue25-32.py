from pathlib import Path
import subprocess

expected = {
    'src/ProxyToAnyConnect/Proxy/ProxyServer.cs': '8f8692442e84f779d04c282770acf6c622c0504e',
    'src/ProxyToAnyConnect/Network/L2tpDnsResolver.cs': '38f976ea90b3b76d2aa9692c2ac4822f63957e0e',
    'tests/ProxyToAnyConnect.SelfTests/CombinedTestRunner.cs': '8d071b019ec3b6e29f01cc98e500835a9ef6e8a5',
    'tests/ProxyToAnyConnect.SelfTests/ProxyConnectAuthoritySelfTests.cs': '6a77104ec6b6d0b82350a296a8a408b7e70b802f',
    'tests/ProxyToAnyConnect.SelfTests/ProxyConnectPortGrammarSelfTests.cs': 'ccca4399cbc3734c20bf997435ee361c5c399be6',
}

for path, sha in expected.items():
    actual = subprocess.check_output(['git', 'rev-parse', f'HEAD:{path}'], text=True).strip()
    if actual != sha:
        raise SystemExit(f'unexpected input blob for {path}: {actual}')


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
    new = new_lf.replace(b'\n', newline)
    return data.replace(old, new)


proxy = Path('src/ProxyToAnyConnect/Proxy/ProxyServer.cs')
data = proxy.read_bytes()

# Avoid an escape-sensitive C# backslash character literal in the inherited #25 source.
lines = data.splitlines(keepends=True)
matches = [
    i for i, line in enumerate(lines)
    if b"character is '@' or '/' or '?' or '#'" in line
]
if len(matches) != 1:
    raise SystemExit(f'expected one forbidden-host-character line, got {len(matches)}')
i = matches[0]
line = lines[i]
newline = b'\r\n' if line.endswith(b'\r\n') else b'\n'
indent = b'                '
lines[i] = (
    indent + b'character == (char)0x5C ||' + newline +
    indent + b"character is '@' or '/' or '?' or '#' or ':')" + newline
)
data = b''.join(lines)

data = replace_block(
    data,
    b'''        var separator = authority.IndexOf(':');
        if (separator >= 0 && authority.IndexOf(':', separator + 1) >= 0)
        {
            throw new NotSupportedException("IPv6 proxy targets are not supported yet.");
        }
''',
    b'''        var separator = authority.IndexOf(':');
        if (separator >= 0 && authority.IndexOf(':', separator + 1) >= 0)
        {
            throw new InvalidDataException($"Invalid CONNECT target '{authority}'.");
        }
''',
    'unbracketed multi-colon block')

data = replace_block(
    data,
    b'''        var host = separator < 0 ? authority : authority[..separator];
        var port = defaultPort;
        if (separator >= 0 &&
            (!int.TryParse(authority[(separator + 1)..], out port) || port is < 1 or > 65535))
        {
            throw new InvalidDataException($"Invalid CONNECT target '{authority}'.");
        }
''',
    b'''        var host = separator < 0 ? authority : authority[..separator];
        var port = defaultPort;
        if (separator >= 0 && !TryParseConnectPort(authority.AsSpan(separator + 1), out port))
        {
            throw new InvalidDataException($"Invalid CONNECT target '{authority}'.");
        }
''',
    'CONNECT port parse block')

data = replace_block(
    data,
    b'''        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException($"Invalid CONNECT target '{authority}'.", ex);
        }
    }

    private static Uri ParseAbsoluteHttpUri''',
    b'''        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
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
            if (character < 0x30 || character > 0x39)
            {
                return false;
            }

            parsed = parsed * 10 + (character - 0x30);
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

    private static Uri ParseAbsoluteHttpUri''',
    'CONNECT port helper marker')
proxy.write_bytes(data)

runner = Path('tests/ProxyToAnyConnect.SelfTests/CombinedTestRunner.cs')
data = runner.read_bytes()
data = replace_block(
    data,
    b'        await RunAsync(nameof(ProxyConnectAuthoritySelfTests), ProxyConnectAuthoritySelfTests.RunAsync);\n',
    b'''        await RunAsync(nameof(ProxyConnectAuthoritySelfTests), ProxyConnectAuthoritySelfTests.RunAsync);
        await RunAsync(nameof(ProxyConnectPortGrammarSelfTests), ProxyConnectPortGrammarSelfTests.RunAsync);
''',
    'CONNECT authority runner anchor')
runner.write_bytes(data)
