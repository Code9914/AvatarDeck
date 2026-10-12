using System.Text.Json;
using AvatarDeck.Core.Parameters;
using AvatarDeck.Osc.OscQuery;

namespace AvatarDeck.Osc.Tests;

public class VrcOscQueryClientTests
{
    // Shape of VRChat's real /avatar response (values invented).
    private const string AvatarJson = """
    {"FULL_PATH":"/avatar","ACCESS":0,"CONTENTS":{
      "change":{"FULL_PATH":"/avatar/change","ACCESS":3,"TYPE":"s","VALUE":["avtr_abc"]},
      "name":{"FULL_PATH":"/avatar/name","ACCESS":1,"TYPE":"s","VALUE":["Xavion"]},
      "eyeheight":{"FULL_PATH":"/avatar/eyeheight","ACCESS":3,"TYPE":"f","VALUE":[1.75]},
      "parameters":{"FULL_PATH":"/avatar/parameters","ACCESS":0,"CONTENTS":{
        "Hoodie":{"FULL_PATH":"/avatar/parameters/Hoodie","ACCESS":3,"TYPE":"T","VALUE":[true]},
        "Jacket":{"FULL_PATH":"/avatar/parameters/Jacket","ACCESS":3,"TYPE":"F","VALUE":[false]},
        "Outfit":{"FULL_PATH":"/avatar/parameters/Outfit","ACCESS":3,"TYPE":"i","VALUE":[2]},
        "Hair_hue":{"FULL_PATH":"/avatar/parameters/Hair_hue","ACCESS":3,"TYPE":"f","VALUE":[0.196585178]},
        "VelocityX":{"FULL_PATH":"/avatar/parameters/VelocityX","ACCESS":1,"TYPE":"f","VALUE":[0.0]},
        "Go":{"FULL_PATH":"/avatar/parameters/Go","ACCESS":0,"CONTENTS":{
          "Wings":{"FULL_PATH":"/avatar/parameters/Go/Wings","ACCESS":3,"TYPE":"T","VALUE":[true]}}}
      }}
    }}
    """;

    [Fact]
    public void Parses_avatar_and_every_parameter_value_including_nested_ones()
    {
        using var document = JsonDocument.Parse(AvatarJson);

        var snapshot = VrcOscQueryClient.Parse(document.RootElement);

        snapshot.AvatarId.Should().Be("avtr_abc");
        snapshot.AvatarName.Should().Be("Xavion");
        var values = snapshot.Parameters.ToDictionary(p => p.Definition.Name, p => p.Value);
        values.Keys.Should().BeEquivalentTo("Hoodie", "Jacket", "Outfit", "Hair_hue", "VelocityX", "Go/Wings");
        values["Hoodie"].Should().Be(OscValue.FromBool(true));
        values["Jacket"].Should().Be(OscValue.FromBool(false));
        values["Outfit"].Should().Be(OscValue.FromInt(2));
        values["Hair_hue"]!.Value.AsFloat().Should().BeApproximately(0.1966f, 0.001f);
        values["Go/Wings"].Should().Be(OscValue.FromBool(true));
        snapshot.Parameters.Single(p => p.Definition.Name == "VelocityX").Definition.IsWritable.Should().BeFalse();
    }

    [Fact]
    public void Finds_last_advertised_port_in_vrchat_log()
    {
        const string log = """
            2026.10.12 16:13:26 Debug      -  Starting
            2026.10.12 16:13:32 Debug      -  Advertising Service VRChat-Client-CE9585 of type OSCQuery on 53918
            2026.10.12 16:13:32 Debug      -  Advertising Service VRChat-Client-CE9585 of type OSC on 9000
            2026.10.12 18:00:00 Debug      -  Advertising Service VRChat-Client-AB12CD of type OSCQuery on 61000
            """;

        VrcOscQueryClient.FindPortInLog(new StringReader(log)).Should().Be(61000);
        VrcOscQueryClient.FindPortInLog(new StringReader("nothing here")).Should().BeNull();
    }
}
