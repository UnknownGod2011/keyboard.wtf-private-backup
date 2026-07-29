namespace KeyboardWtf.Services.BrowserAutomationV2;

using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

internal static class BrowserNativeMessagingHost
{
    private const int MaxMessageBytes = 32 * 1024 * 1024;

    public static bool IsNativeHostInvocation(string[] args)
    {
        if (args == null || args.Length == 0)
            return false;

        return args.Any(arg =>
            string.Equals(arg, "--browser-native-host", StringComparison.OrdinalIgnoreCase)
            || BrowserHelperProtocol.AllowedExtensionOrigins.Any(origin =>
                arg.StartsWith(origin, StringComparison.OrdinalIgnoreCase)));
    }

    public static int Run()
    {
        try
        {
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();
            using var pipe = ConnectToMainApp();

            while (true)
            {
                var lengthBytes = ReadExactly(input, sizeof(int), allowEndOfStream: true);
                if (lengthBytes == null)
                    return 0;

                var length = BitConverter.ToInt32(lengthBytes, 0);
                if (length is <= 0 or > MaxMessageBytes)
                    return 2;

                var payload = ReadExactly(input, length, allowEndOfStream: false);
                WriteFrame(pipe, payload);
                var response = ReadFrame(pipe);
                WriteFrame(output, response);
                output.Flush();
            }
        }
        catch
        {
            // Native messaging stdout must contain framed protocol messages only.
            return 1;
        }
    }

    private static NamedPipeClientStream ConnectToMainApp()
    {
        var pipe = TryConnect(TimeSpan.FromMilliseconds(600));
        if (pipe != null)
            return pipe;

        TryStartMainApp();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            pipe = TryConnect(TimeSpan.FromMilliseconds(500));
            if (pipe != null)
                return pipe;
            Thread.Sleep(250);
        }

        throw new IOException("keyboard.wtf browser bridge is unavailable");
    }

    private static NamedPipeClientStream TryConnect(TimeSpan timeout)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            BrowserHelperProtocol.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            pipe.Connect((int)timeout.TotalMilliseconds);
            return pipe;
        }
        catch
        {
            pipe.Dispose();
            return null;
        }
    }

    private static void TryStartMainApp()
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
                return;
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
        }
        catch
        {
            // The service worker will retry and Settings will show the real failure.
        }
    }

    internal static byte[] ReadFrame(Stream stream)
    {
        var lengthBytes = ReadExactly(stream, sizeof(int), allowEndOfStream: false);
        var length = BitConverter.ToInt32(lengthBytes, 0);
        if (length is <= 0 or > MaxMessageBytes)
            throw new InvalidDataException("Invalid browser bridge frame length");
        return ReadExactly(stream, length, allowEndOfStream: false);
    }

    internal static void WriteFrame(Stream stream, byte[] payload)
    {
        payload ??= Encoding.UTF8.GetBytes("{}");
        stream.Write(BitConverter.GetBytes(payload.Length));
        stream.Write(payload);
        stream.Flush();
    }

    private static byte[] ReadExactly(Stream stream, int length, bool allowEndOfStream)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = stream.Read(buffer, offset, length - offset);
            if (read == 0)
            {
                if (allowEndOfStream && offset == 0)
                    return null;
                throw new EndOfStreamException();
            }
            offset += read;
        }
        return buffer;
    }
}
