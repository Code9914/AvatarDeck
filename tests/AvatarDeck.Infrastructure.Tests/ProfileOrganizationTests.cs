using AvatarDeck.Core.Abstractions;
using AvatarDeck.Core.Models;
using AvatarDeck.Core.Parameters;
using AvatarDeck.Core.Services;

namespace AvatarDeck.Infrastructure.Tests;

/// <summary>Auto-organisation rules on a parameter list shaped like a real VRCFury / GoGo Loco avatar.</summary>
public class ProfileOrganizationTests
{
    private static readonly AvatarDefinition Avatar = new("avtr_x", "Fox",
    [
        // Menu toggles under a creator prefix → flattened into one folder
        B("A3C/Toggle/Tail"), B("A3C/Toggle/Horns"),
        // Plain customisation sliders
        F("Hair_hue"), F("Hair_swap"), F("Top_hue"),
        // VRCFury local copies, compressor internals and version markers → dropped
        F("VF57_Hair_hue"), I("VF57_SyncDataNum0"), B("VF57_SyncIndex1"), B("VF_1.1426.0"), B("VF_N"),
        // VRCFury prefab with flat parameters → "Extras"
        B("VF134_Flashlight"), F("VF134_Flashlight_Range"), B("VF134_Zoom_Toggle"),
        // VRCFury prefab with a path → folder name without prefix
        B("VF132_Shockwave/Particles"),
        // System → hidden "System" folder
        B("Go/Crouch"), F("Smoothing/Local"), B("State/TrackingActive"), B("EyeTrackingActive"), I("VF34_TC_current_trackingEyes"),
        F("VF34_TC_FullControllerBuilder.Apply_on_X_(Pc/FaceTracking)/Eyes"),
    ]);

    [Fact]
    public void Organizes_folders_like_a_human_would()
    {
        var profile = ProfileBuilder.Build(Avatar, ProfileSource.VrcOscConfig);

        profile.Folders.Select(f => f.Name).Should().Equal("A3C Toggle", "Shockwave", "Avatar", "Extras", "System");
        Folder("A3C Toggle").Widgets.Select(w => w.Label).Should().Equal("Horns", "Tail");
        Folder("Avatar").Widgets.Select(w => w.Parameter).Should().Equal("Hair_hue", "Hair_swap", "Top_hue");
        Folder("Extras").Widgets.Select(w => w.Label).Should().Equal("Flashlight", "Flashlight Range", "Zoom Toggle");

        var system = Folder("System");
        system.Hidden.Should().BeTrue();
        system.Children.Select(c => c.Name).Should().BeEquivalentTo("GoGo Loco", "Smoothing", "Tracking");
        system.AllWidgets().Should().HaveCount(6);

        profile.AllWidgets().Select(w => w.Parameter).Should().NotContain(p => p.StartsWith("VF57_") || p.StartsWith("VF_"));
        profile.Folders.Should().OnlyContain(f => f.Icon != null && f.Color != null);
        profile.GeneratorVersion.Should().Be(ProfileBuilder.GeneratorVersion);

        Folder Folder(string name) => profile.Folders.Single(f => f.Name == name);
    }

    [Fact]
    public void Reorganize_keeps_presets_and_favorites_by_parameter()
    {
        var old = new AvatarProfile
        {
            AvatarId = "avtr_x",
            Name = "My Fox",
            Folders = [new Folder { Name = "Parameters", Widgets = [new ToggleWidget { Id = "old1", Parameter = "A3C/Toggle/Tail" }] }],
            Favorites = ["old1"],
            Presets = [new Preset { Name = "Casual" }],
        };

        var rebuilt = ProfileBuilder.Reorganize(old, Avatar);

        rebuilt.Name.Should().Be("My Fox");
        rebuilt.Presets.Should().ContainSingle(p => p.Name == "Casual");
        rebuilt.Favorites.Should().ContainSingle().Which.Should().Be(rebuilt.AllWidgets().Single(w => w.Parameter == "A3C/Toggle/Tail").Id);
    }

    [Fact]
    public void Merge_into_customized_profile_does_not_restructure_it()
    {
        var profile = ProfileBuilder.Build(Avatar with { Parameters = [B("A3C/Toggle/Tail")] }, ProfileSource.VrcOscConfig);
        profile.IsCustomized = true;
        profile.Folders.Add(new Folder { Name = "Wrapper", Children = [new Folder { Name = "Only child" }] });

        ProfileBuilder.MergeMissing(profile, Avatar with { Parameters = [B("A3C/Toggle/Tail"), F("Hair_hue")] });

        profile.Folders.Select(f => f.Name).Should().Contain("Wrapper", "a customised layout is never flattened");
    }

    [Theory]
    [InlineData("VF 1.1190.0")]                // version marker with a space
    [InlineData("VF/BrokenWd")]                // VRCFury flag
    [InlineData("VF54_Hairhue/LastSynced")]    // parameter compressor
    [InlineData("VF54_Outfits/Current")]
    [InlineData("VF54_SyncPointer")]
    public void Drops_vrcfury_internals_seen_on_real_avatars(string name)
    {
        var profile = ProfileBuilder.Build(new AvatarDefinition("a", "a", [B(name), B("Hat")]), ProfileSource.VrcOscConfig);

        profile.AllWidgets().Select(w => w.Parameter).Should().Equal("Hat");
    }

    [Theory]
    [InlineData("isIdle", "Locomotion")]
    [InlineData("resetLoco", "Locomotion")]
    [InlineData("LocoGroundFlap", "Locomotion")]
    [InlineData("VF94_BlinkTriggerSynced", "Sync")]
    [InlineData("Bonk_Sync", "Sync")]
    [InlineData("VF70_TC_VRC Default_trackingHead", "Tracking")]
    public void Hides_animator_internals_in_system(string name, string subFolder)
    {
        var profile = ProfileBuilder.Build(new AvatarDefinition("a", "a", [B(name), B("Hat")]), ProfileSource.VrcOscConfig);

        var system = profile.Folders.Single(f => f.Name == ProfileBuilder.SystemFolderName);
        system.Hidden.Should().BeTrue();
        system.Children.Single(c => c.Name == subFolder).Widgets.Single().Parameter.Should().Be(name);
    }

    [Fact]
    public void Puts_all_flat_vrcfury_prefabs_in_one_extras_folder_and_splits_dotted_names()
    {
        var profile = ProfileBuilder.Build(
            new AvatarDefinition("a", "a", [B("VF99_id"), B("VF116_invis"), B("Ragdoll.Audio1")]), ProfileSource.VrcOscConfig);

        profile.Folders.Single(f => f.Name == "Extras").Widgets.Should().HaveCount(2);
        profile.AllWidgets().Single(w => w.Parameter == "Ragdoll.Audio1").Label.Should().Be("Ragdoll Audio 1");
    }

    [Fact]
    public void Repairs_widgets_bound_to_names_with_spaces()
    {
        var profile = ProfileBuilder.Build(new AvatarDefinition("a", "a", [F("Hair_hue")]), ProfileSource.VrcOscConfig);
        var good = profile.AllWidgets().Single();
        var duplicate = new SliderWidget { Parameter = "Hair hue" };
        var orphan = new ToggleWidget { Parameter = "Invis Fix ON" };
        profile.Folders[0].Widgets.AddRange([duplicate, orphan]);
        profile.Favorites.Add(duplicate.Id);

        ProfileBuilder.RepairAddresses(profile).Should().Be(2);

        profile.AllWidgets().Should().HaveCount(2).And.Contain(good);
        orphan.Parameter.Should().Be("Invis_Fix_ON");
        profile.Favorites.Should().NotContain(duplicate.Id);
    }

    [Theory]
    [InlineData("Hair Hue", "hair")]
    [InlineData("Tops", "top")]
    [InlineData("Top hue", "top")]
    [InlineData("Flashlight Range", "flashlight")]
    public void Section_keys_group_by_first_word(string label, string key) => ProfileBuilder.SectionKey(label).Should().Be(key);

    private static ParameterDefinition B(string name) => new(name, ParameterType.Bool);

    private static ParameterDefinition F(string name) => new(name, ParameterType.Float);

    private static ParameterDefinition I(string name) => new(name, ParameterType.Int);
}
