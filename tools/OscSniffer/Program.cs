// OscSniffer — pretends to be VRChat for development without the game.
//
//   dotnet run --project tools/OscSniffer -- [--listen 9000] [--reply 9001] [--avatar avtr_demo] [--duration 0]
//
// * prints every OSC message AvatarDeck sends (VRChat's input port, 9000)
// * optionally sends /avatar/change to AvatarDeck (VRChat's output port, 9001)
// * echoes every received parameter back, like VRChat does, so the UI feedback loop can be tested.
using System.Net;
using System.Net.Sockets;
using AvatarDeck.Osc;

var listenPort = GetInt("--listen", 9000);
var replyPort = GetInt("--reply", 9001);
var avatar = GetString("--avatar");
var duration = GetInt("--duration", 0);

using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, listenPort));
var reply = new IPEndPoint(IPAddress.Loopback, replyPort);
Console.WriteLine($"Listening on 127.0.0.1:{listenPort}, replying to {reply}. Ctrl+C to quit.");

if (avatar is not null)
{
    await SendAsync("/avatar/change", OscArgument.String(avatar));
    Console.WriteLine($"→ /avatar/change \"{avatar}\"");
}

using var cts = duration > 0 ? new CancellationTokenSource(TimeSpan.FromSeconds(duration)) : new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var messages = new List<OscMessage>();
try
{
    while (true)
    {
        var packet = await socket.ReceiveAsync(cts.Token);
        messages.Clear();
        OscCodec.TryDecode(packet.Buffer, messages);
        foreach (var message in messages)
        {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} ← {message}");
            if (message.Arguments.Length == 1)
            {
                await SendAsync(message.Address, message.Arguments[0]); // VRChat-style echo
            }
        }
    }
}
catch (OperationCanceledException)
{
}

async Task SendAsync(string address, OscArgument argument)
{
    var buffer = new byte[OscCodec.GetEncodedSize(address, [argument])];
    OscCodec.Encode(address, [argument], buffer);
    await socket.SendAsync(buffer, reply);
}

string? GetString(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

int GetInt(string name, int fallback) => int.TryParse(GetString(name), out var v) ? v : fallback;
