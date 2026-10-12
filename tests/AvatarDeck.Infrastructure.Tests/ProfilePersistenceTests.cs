using AvatarDeck.Core.Models;
using AvatarDeck.Core.Parameters;
using AvatarDeck.Infrastructure.Profiles;
using AvatarDeck.Infrastructure.VrChat;
using Microsoft.Extensions.Logging.Abstractions;

namespace AvatarDeck.Infrastructure.Tests;

public sealed class ProfilePersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AvatarDeckTests", Guid.NewGuid().ToString("N"));
    private readonly JsonProfileStore _store;

    public ProfilePersistenceTests()
    {
        _store = new JsonProfileStore(new AppPaths(_root), NullLogger<JsonProfileStore>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Round_trips_polymorphic_widgets_presets_and_values()
    {
        var profile = new AvatarProfile
        {
            AvatarId = "avtr_123",
            Name = "Fox",
            Folders =
            [
                new Folder
                {
                    Name = "Clothes",
                    Widgets =
                    [
                        new ToggleWidget { Label = "Hoodie", Parameter = "Hoodie" },
                        new SliderWidget { Label = "Hair Hue", Parameter = "HairHue", Maximum = 1f },
                        new DropdownWidget { Label = "Outfit", Parameter = "Outfit", Options = [new DropdownOption { Label = "Casual", Value = 2 }] },
                        new ButtonWidget { Label = "Wave", Parameter = "Wave", HoldMilliseconds = 300 },
                    ],
                    Children = [new Folder { Name = "Shoes", Widgets = [new TwoAxisWidget { Parameter = "X", ParameterY = "Y" }] }],
                },
            ],
            Presets = [new Preset { Name = "Casual", Values = [new PresetEntry { Parameter = "HairHue", Value = OscValue.FromFloat(1f) }] }],
        };
        profile.Favorites.Add(profile.Folders[0].Widgets[0].Id);

        await _store.SaveAsync(profile);
        var loaded = await _store.LoadAsync("avtr_123");

        loaded.Should().BeEquivalentTo(profile, o => o.RespectingRuntimeTypes());
        loaded!.Presets[0].Values[0].Value.Type.Should().Be(ParameterType.Float, "1.0 must not be read back as int 1");
    }

    [Fact]
    public async Task Writes_human_readable_json()
    {
        var profile = new AvatarProfile { AvatarId = "avtr_json", Folders = [new Folder { Name = "Body", Widgets = [new ToggleWidget { Parameter = "Tail" }] }] };
        await _store.SaveAsync(profile);

        var json = await File.ReadAllTextAsync(Path.Combine(_root, "avatars", "avtr_json.json"));
        json.Should().Contain("\"kind\": \"toggle\"").And.Contain("\"onValue\": true").And.Contain("\"parameterType\": \"Bool\"");
    }

    [Fact]
    public async Task List_returns_summaries_and_ignores_corrupt_files()
    {
        await _store.SaveAsync(new AvatarProfile { AvatarId = "avtr_a", Name = "A" });
        await File.WriteAllTextAsync(Path.Combine(_root, "avatars", "broken.json"), "{ not json");

        var list = await _store.ListAsync();

        list.Should().ContainSingle().Which.Name.Should().Be("A");
    }

    [Theory]
    [InlineData("../../evil", "______evil")]
    [InlineData("avtr_ok-1", "avtr_ok-1")]
    public void Sanitizes_avatar_ids_used_as_file_names(string id, string expected)
    {
        JsonProfileStore.ToFileName(id).Should().Be(expected);
    }

    [Fact]
    public async Task Reads_vrchat_osc_config_and_marks_read_only_parameters()
    {
        var reader = new VrcOscConfigReader(Path.Combine(AppContext.BaseDirectory, "Fixtures", "OSC"), NullLogger<VrcOscConfigReader>.Instance);

        var definition = await reader.TryGetAsync("avtr_fixture");

        definition.Should().NotBeNull();
        definition!.Name.Should().Be("Fixture Fox");
        definition.Parameters.Should().Contain(new ParameterDefinition("VelocityX", ParameterType.Float, IsWritable: false));
        definition.Parameters.Should().Contain(new ParameterDefinition("Clothes/Hoodie", ParameterType.Bool));
        (await reader.TryGetAsync("avtr_missing")).Should().BeNull();
    }
}
