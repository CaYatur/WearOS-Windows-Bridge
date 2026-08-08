using Bridge.Protocol;
using Xunit;

namespace Bridge.Protocol.Tests;

public class ProtocolTests
{
    [Fact] public void Signed_message_verifies_and_roundtrips()
    {
        var key=BridgeCodec.NewPairingKey();
        var env=BridgeCodec.Sign(BridgeMessageType.State,new BridgePayload(BridgeFeature.Media,new MediaState("Song","Artist",null,"Test",true,100,1000)),key);
        var decoded=BridgeCodec.Deserialize(BridgeCodec.Serialize(env));
        Assert.NotNull(decoded); Assert.True(BridgeCodec.Verify(decoded!,key,TimeSpan.FromMinutes(1))); Assert.Equal("Song",decoded!.Payload.Media!.Title);
    }
    [Fact] public void Tampered_message_is_rejected()
    {
        var key=BridgeCodec.NewPairingKey();
        var env=BridgeCodec.Sign(BridgeMessageType.Ping,new BridgePayload(BridgeFeature.Media),key);
        var tampered=env with { Payload=new BridgePayload(BridgeFeature.Clipboard) };
        Assert.False(BridgeCodec.Verify(tampered,key,TimeSpan.FromMinutes(1)));
    }
    [Fact] public void Old_message_is_rejected()
    {
        var key=BridgeCodec.NewPairingKey();
        var env=BridgeCodec.Sign(BridgeMessageType.Ping,new BridgePayload(BridgeFeature.Media),key,DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds());
        Assert.False(BridgeCodec.Verify(env,key,TimeSpan.FromMinutes(1)));
    }
}
