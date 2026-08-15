using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TerminalStudio.Services;

public static class NetworkUtils
{
    public static async Task<bool> CheckProxyAvailableAsync(string address, int timeoutMs = 250)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;

        try
        {
            string host = "127.0.0.1";
            int port = 10809;

            string clean = address.Trim();
            if (clean.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) clean = clean.Substring(7);
            else if (clean.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) clean = clean.Substring(8);
            else if (clean.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase)) clean = clean.Substring(9);
            else if (clean.StartsWith("socks://", StringComparison.OrdinalIgnoreCase)) clean = clean.Substring(8);

            int colonIndex = clean.IndexOf(':');
            if (colonIndex != -1)
            {
                host = clean.Substring(0, colonIndex);
                string portStr = clean.Substring(colonIndex + 1).TrimEnd('/');
                if (int.TryParse(portStr, out int p))
                {
                    port = p;
                }
            }
            else
            {
                host = clean;
            }

            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(timeoutMs);
            await client.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
