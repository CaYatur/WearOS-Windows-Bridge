using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Bridge.Windows;

public sealed class AutoPairingServer : IDisposable
{
    public const int Port=38473;
    private readonly byte[] _key;
    private readonly CancellationTokenSource _cts=new();
    private TcpListener? _listener;
    public AutoPairingServer(byte[] key)=>_key=key;
    public void Start(){_listener=new TcpListener(IPAddress.Any,Port);_listener.Start();_=RunAsync();}
    private async Task RunAsync()
    {
        while(!_cts.IsCancellationRequested) try
        {
            var client=await _listener!.AcceptTcpClientAsync(_cts.Token);
            _=HandleAsync(client);
        } catch(OperationCanceledException){break;} catch(Exception ex){Console.Error.WriteLine($"Pairing: {ex.Message}");}
    }
    private async Task HandleAsync(TcpClient client)
    {
        using(client) try
        {
            using var reader=new StreamReader(client.GetStream(),Encoding.UTF8,false,1024,true);
            using var writer=new StreamWriter(client.GetStream(),new UTF8Encoding(false),1024,true){AutoFlush=true};
            var line=await reader.ReadLineAsync();
            if(line is null)return;
            using var doc=JsonDocument.Parse(line);
            if(doc.RootElement.GetProperty("type").GetString()!="pair_request")return;
            var name=doc.RootElement.TryGetProperty("name",out var n)?n.GetString()??"Unknown device":"Unknown device";
            var approved=false;
            using(var done=new ManualResetEventSlim())
            {
                // MessageBox must run on an STA thread. ThreadPool threads are MTA and the
                // approval prompt could silently fail/not become visible on some systems.
                var promptThread=new Thread(()=>
                {
                    try
                    {
                        approved=MessageBox.Show(
                            $"Allow pairing with {name}?\n\nOnly approve devices you recognize.",
                            "WearOS Windows Bridge",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Question,
                            MessageBoxDefaultButton.Button1,
                            MessageBoxOptions.DefaultDesktopOnly)==DialogResult.Yes;
                    }
                    finally { done.Set(); }
                });
                promptThread.IsBackground=true;
                promptThread.SetApartmentState(ApartmentState.STA);
                promptThread.Start();
                done.Wait(TimeSpan.FromSeconds(60));
            }
            var payload=approved
                ? JsonSerializer.Serialize(new{type="pair_ok",key=Convert.ToBase64String(_key)})
                : JsonSerializer.Serialize(new{type="pair_denied"});
            await writer.WriteLineAsync(payload);
        } catch(Exception ex){Console.Error.WriteLine($"Pairing client: {ex.Message}");}
    }
    public void Dispose(){_cts.Cancel();_listener?.Stop();_cts.Dispose();}
}
