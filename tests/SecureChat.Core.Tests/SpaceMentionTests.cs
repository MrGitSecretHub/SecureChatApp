using SecureChat.Core.Contracts;
using Xunit;

namespace SecureChat.Core.Tests;

public sealed class SpaceMentionTests
{
    [Fact]
    public void Parse_FindsOnlyKnownMembers()
    {
        var text = "Привет @anna и @bob, не забудьте @anna! А кто такой @stranger и @sure?";

        var mentions = SpaceMentions.Parse(
            text,
            new[] { "Anna", "bob", "sureword" });

        Assert.Equal(2, mentions.Count);
        Assert.Equal("Anna", mentions[0]); // канонический логин участника
        Assert.Equal("bob", mentions[1]);
    }

    [Fact]
    public void Parse_IgnoresSimilarTokens()
    {
        var mentions = SpaceMentions.Parse(
            "mail: test@example.com, @Anna, и mail@example",
            new[] { "anna" });

        // test@example и mail@example не начинаются с @, а example.com — не логин.
        var single = Assert.Single(mentions);
        Assert.Equal("anna", single);
    }

    [Fact]
    public void Parse_HandlesEdgeTokens()
    {
        var mentions = SpaceMentions.Parse(
            "@1 @a_1 e-mail@user😾 @user1",
            new[] { "1", "a_1", "user", "user1" });

        // «@@@»-варианты отброшены: перед «user» стоит @ (исключаем повтор @)
        Assert.Equal(new[] { "1", "a_1", "user1" }, mentions);
    }

    [Fact]
    public void Parse_EmptyText_OrEmptyMembers_ReturnsEmpty()
    {
        Assert.Empty(SpaceMentions.Parse(null, new[] { "anna" }));
        Assert.Empty(SpaceMentions.Parse("", new[] { "anna" }));
        Assert.Empty(SpaceMentions.Parse("@anna", Array.Empty<string>()));
    }

    [Fact]
    public void Parse_LimitedToMaxMentions()
    {
        var known = Enumerable.Range(0, 30).Select(i => $"user{i}").ToArray();
        var text = "@" + string.Join(" @", known);

        var mentions = SpaceMentions.Parse(text, known);

        Assert.Equal(SpaceMentions.MaxMentions, mentions.Count);
    }

    [Fact]
    public void HasMentionFor_IsCaseInsensitive()
    {
        var payload = new SpaceMessagePayload { Mentions = ["Anna"] };

        Assert.True(SpaceMentions.HasMentionFor(payload, "anna"));
        Assert.False(SpaceMentions.HasMentionFor(payload, "bob"));
        Assert.False(SpaceMentions.HasMentionFor(null, "anna"));
    }

    [Fact]
    public void Split_ProducesPlainAndMentionSegments()
    {
        var segments = SpaceMentions.Split("привет @Anna, привет @stranger", new[] { "Anna" });

        Assert.Equal(4, segments.Count);
        Assert.Equal("привет ", segments[0].Text);
        Assert.Null(segments[0].Mention);
        Assert.Equal("@Anna", segments[1].Text);
        Assert.NotNull(segments[1].Mention);
        Assert.Equal("@stranger", segments[3].Text);
        Assert.Null(segments[3].Mention); // не участник — без подсветки
    }

    [Fact]
    public void Payload_RoundTrip_KeepsMentionsAndCommentId()
    {
        var payload = new SpaceMessagePayload
        {
            Kind = "text",
            Text = "смотрите @bob",
            Mentions = ["bob"],
            CommentToMessageId = "post-42"
        };

        var decoded = SpacePayloadCodec.Decode(SpacePayloadCodec.Encode(payload));

        Assert.Single(decoded.Mentions);
        Assert.Equal("bob", decoded.Mentions[0]);
        Assert.Equal("post-42", decoded.CommentToMessageId);
    }

    [Fact]
    public void Payload_OldWithoutComments_DecodesWithDefaults()
    {
        // payload старого клиента без Mentions/CommentToMessageId
        var json = "\u0001sp1:" +
            "{\"Kind\":\"text\",\"Text\":\"привет\",\"ReplyToMessageId\":null," +
            "\"ReplyToText\":null,\"IsForwarded\":false,\"Media\":null}";

        var decoded = SpacePayloadCodec.Decode(json);

        Assert.Equal("привет", decoded.Text);
        Assert.Empty(decoded.Mentions);
        Assert.Null(decoded.CommentToMessageId);
    }
}
