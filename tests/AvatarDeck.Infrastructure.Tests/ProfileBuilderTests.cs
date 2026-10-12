using AvatarDeck.Core.Abstractions;
using AvatarDeck.Core.Models;
using AvatarDeck.Core.Parameters;
using AvatarDeck.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AvatarDeck.Infrastructure.Tests;

public class ProfileBuilderTests
{
    private static readonly AvatarDefinition Definition = new("avtr_x", "Fox",
    [
        new("Clothes/Hoodie", ParameterType.Bool),
        new("Clothes/Jacket", ParameterType.Bool),
        new("Body/Tail", ParameterType.Bool),
        new("HairHue", ParameterType.Float),
        new("Outfit", ParameterType.Int),
        new("VelocityX", ParameterType.Float, IsWritable: false),
        new("VRCEmote", ParameterType.Int),
        new("Tail_IsGrabbed", ParameterType.Bool),
    ]);

    [Fact]
    public void Builds_folders_from_parameter_paths_and_skips_builtins()
    {
        var profile = ProfileBuilder.Build(Definition, ProfileSource.VrcOscConfig);

        profile.Folders.Select(f => f.Name).Should().BeEquivalentTo("Clothes", "Body", ProfileBuilder.DefaultFolderName);
        profile.Folders.Single(f => f.Name == "Clothes").Widgets.Select(w => w.Label).Should().Equal("Hoodie", "Jacket");
        profile.AllWidgets().Select(w => w.Parameter).Should().NotContain(["VelocityX", "VRCEmote", "Tail_IsGrabbed"]);

        var hue = profile.AllWidgets().Single(w => w.Parameter == "HairHue");
        hue.Should().BeOfType<SliderWidget>().Which.Label.Should().Be("Hair Hue");
        profile.AllWidgets().Single(w => w.Parameter == "Outfit").Should().BeOfType<SliderWidget>().Which.Step.Should().Be(1);
    }

    [Fact]
    public void Merge_adds_only_missing_parameters_and_keeps_user_edits()
    {
        var profile = ProfileBuilder.Build(Definition with { Parameters = [new("Clothes/Hoodie", ParameterType.Bool)] }, ProfileSource.VrcOscConfig);
        profile.AllWidgets().Single().Label = "My Hoodie";

        var added = ProfileBuilder.MergeMissing(profile, Definition);

        added.Should().Be(4);
        profile.AllWidgets().Should().Contain(w => w.Label == "My Hoodie");
        profile.AllWidgets().Count(w => w.Parameter == "Clothes/Hoodie").Should().Be(1);
    }

    [Theory]
    [InlineData("HairHue", "Hair Hue")]
    [InlineData("hair_hue", "Hair hue")]
    [InlineData("NSFW", "NSFW")]
    [InlineData("Toggle-Wings", "Toggle Wings")]
    [InlineData("bit0", "Bit 0")]
    [InlineData("A3C", "A3C")]
    public void Humanizes_names(string input, string expected) => ProfileBuilder.Humanize(input).Should().Be(expected);

    [Fact]
    public async Task ProfileManager_generates_and_saves_profile_on_first_avatar_change()
    {
        var store = Substitute.For<IProfileStore>();
        var feed = Substitute.For<IVrcStateFeed>();
        var source = Substitute.For<IAvatarDefinitionSource>();
        source.TryGetAsync("avtr_x", Arg.Any<CancellationToken>()).Returns(Definition);
        using var manager = new ProfileManager(store, feed, [source], new NotificationService(TimeProvider.System), NullLogger<ProfileManager>.Instance);
        var changed = new TaskCompletionSource<AvatarProfile?>();
        manager.ActiveProfileChanged += (_, p) => changed.TrySetResult(p);

        feed.AvatarChanged += Raise.EventWith(feed, new AvatarChangedEventArgs("avtr_x"));
        var profile = await changed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        profile!.Name.Should().Be("Fox");
        profile.Source.Should().Be(ProfileSource.VrcOscConfig);
        await store.Received(1).SaveAsync(profile, Arg.Any<CancellationToken>());
    }
}
