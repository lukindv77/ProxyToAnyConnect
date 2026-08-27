from pathlib import Path
import subprocess

expected = {
    'src/ProxyToAnyConnect/Proxy/ProxyServer.cs': '920e8c335bc5488dc0f08dae87e4c0247e9b78eb',
    'tests/ProxyToAnyConnect.SelfTests/CombinedTestRunner.cs': '7d1d807e7696a5527e18231814e9f7ebb2e56e79',
    'tests/ProxyToAnyConnect.SelfTests/ProxyConnectionOptionSelfTests.cs': '0b2cca1f4ec732c5e45130fa731dbdc0b44eb292',
    'tests/ProxyToAnyConnect.SelfTests/ProxyParserAllocationSelfTests.cs': 'dcd750848417390d5bf01696e7b66b36c0974d8f',
}
for path, sha in expected.items():
    actual = subprocess.check_output(['git', 'rev-parse', f'HEAD:{path}'], text=True).strip()
    if actual != sha:
        raise SystemExit(f'unexpected #26 input blob for {path}: {actual}')


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
    b'''                var rawValue = line[(separator + 1)..];
                if (!IsValidHeaderValue(rawValue))
                {
                    throw new InvalidDataException("Invalid HTTP header field value.");
                }

                var value = rawValue.Trim();
''',
    b'''                var rawValue = line[(separator + 1)..];
                if (!IsValidHeaderValue(rawValue))
                {
                    throw new InvalidDataException("Invalid HTTP header field value.");
                }

                if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                {
                    ValidateConnectionOptions(rawValue);
                }

                var value = rawValue.Trim();
''',
    'plain-HTTP header validation anchor')

data = replace_block(
    data,
    b'''        private static bool IsValidHeaderValue(ReadOnlySpan<char> value)
        {
            foreach (var character in value)
            {
                if ((character < 0x20 && character != '\\t') || character == 0x7F)
                {
                    return false;
                }
            }

            return true;
        }

        private static void ValidateHeaderLines''',
    b'''        private static bool IsValidHeaderValue(ReadOnlySpan<char> value)
        {
            foreach (var character in value)
            {
                if ((character < 0x20 && character != '\\t') || character == 0x7F)
                {
                    return false;
                }
            }

            return true;
        }

        private static void ValidateConnectionOptions(ReadOnlySpan<char> value)
        {
            var segmentStart = 0;
            while (segmentStart <= value.Length)
            {
                var remaining = value[segmentStart..];
                var comma = remaining.IndexOf(',');
                var segment = comma < 0 ? remaining : remaining[..comma];

                var trimStart = 0;
                while (trimStart < segment.Length &&
                    (segment[trimStart] == ' ' || segment[trimStart] == (char)0x09))
                {
                    trimStart++;
                }

                var trimEnd = segment.Length;
                while (trimEnd > trimStart &&
                    (segment[trimEnd - 1] == ' ' || segment[trimEnd - 1] == (char)0x09))
                {
                    trimEnd--;
                }

                if (!IsValidHeaderName(segment[trimStart..trimEnd]))
                {
                    throw new InvalidDataException("Invalid HTTP Connection option.");
                }

                if (comma < 0)
                {
                    return;
                }

                segmentStart += comma + 1;
            }
        }

        private static void ValidateHeaderLines''',
    'Connection-option helper insertion point')
proxy.write_bytes(data)

runner = Path('tests/ProxyToAnyConnect.SelfTests/CombinedTestRunner.cs')
data = runner.read_bytes()
data = replace_block(
    data,
    b'        await RunAsync(nameof(ProxyHttpHeaderValueSelfTests), ProxyHttpHeaderValueSelfTests.RunAsync);\n',
    b'''        await RunAsync(nameof(ProxyHttpHeaderValueSelfTests), ProxyHttpHeaderValueSelfTests.RunAsync);
        await RunAsync(nameof(ProxyConnectionOptionSelfTests), ProxyConnectionOptionSelfTests.RunAsync);
''',
    'Connection-option runner anchor')
runner.write_bytes(data)

allocation = Path('tests/ProxyToAnyConnect.SelfTests/ProxyParserAllocationSelfTests.cs')
data = allocation.read_bytes()
data = replace_block(
    data,
    b'                "Connection: x-two, , Upgrade\\r\\n" +\n',
    b'                "Connection: x-two, Upgrade\\r\\n" +\n',
    'origin-header valid Connection fixture')
data = replace_block(
    data,
    b'            "Connection: X-One, , x-two, X-One\\r\\n" +\n',
    b'            "Connection: X-One, x-two, X-One\\r\\n" +\n',
    'stack-token valid Connection fixture')
allocation.write_bytes(data)
