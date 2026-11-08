using SecureChat.Core.Contracts;
using Xunit;

namespace SecureChat.Core.Tests;

public sealed class SpacePayloadTests
{
    [Fact]
    public void Encode_ThenDecode_KeepsReplyForwardAndMedia()
    {
        var payload = new SpaceMessagePayload
        {
            Kind = "image",
            Text = "Фото",
            ReplyToMessageId = "m1",
            ReplyToText = "привет",
            IsForwarded = true,
            Media = new SpaceMediaAttachment
            {
                Id = "media-1",
                FileName = "a.jpg",
                ContentType = "image/jpeg",
                Size = 42,
                Key = "k",
                Nonce = "n",
                Tag = "t"
            }
        };

        var decoded = SpacePayloadCodec.Decode(SpacePayloadCodec.Encode(payload));

        Assert.Equal("image", decoded.Kind);
        Assert.Equal("Фото", decoded.Text);
        Assert.Equal("m1", decoded.ReplyToMessageId);
        Assert.True(decoded.IsForwarded);
        Assert.Equal("media-1", decoded.Media!.Id);
        Assert.Equal(42, decoded.Media!.Size);
    }

    [Fact]
    public void Decode_LegacyPlainText_BecomesTextMessage()
    {
        var decoded = SpacePayloadCodec.Decode("обычный текст");

        Assert.Equal("text", decoded.Kind);
        Assert.Equal("обычный текст", decoded.Text);
        Assert.Null(decoded.Media);
    }
}
