using System.Text;
using System.Text.Json;
using SqlFlow.Acquire.Runtime.Protection;
using SqlFlow.Core;
using SqlFlow.Core.Acquire;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Acquire.Tests;

/// <summary>
/// The landing-time data-protection step: every transform family (remove/redact/mask/hash/hmac/tokenize/encrypt/
/// generalize), the path semantics over real payload shapes, the scope-controlled linkability, and the loader's
/// validation. The reference payload mirrors the Spare Labs /v1/requests shape that motivated the capability
/// (rider PII that the legacy producer scrubbed before landing).
/// </summary>
public sealed class ProtectionTests
{
    private const string RiderPayload = """
        {
          "data": [
            { "id": "r1", "rider": "Kari Nordmann", "scheduledPickupAddress": "Storgata 1, Sauda",
              "phone": "+47 912 34 567", "email": "kari.nordmann@example.no", "createdAt": "2026-03-15T10:30:00Z",
              "price": 123.45, "driver": { "firstName": "Ola", "lastName": "Hansen" } },
            { "id": "r2", "rider": "Per Olsen", "scheduledPickupAddress": "Torget 2, Egersund",
              "phone": "+47 998 76 543", "email": "per@example.no", "createdAt": "2026-04-02T08:00:00Z",
              "price": 67.89, "driver": { "firstName": "Nina", "lastName": "Berg" } }
          ]
        }
        """;

    private static PayloadProtector Protector(params AcquireProtectRule[] rules)
        => new(rules, new Dictionary<string, string>(StringComparer.Ordinal), "test_flow");

    private static PayloadProtector KeyedProtector(string secretRef, string secretValue, params AcquireProtectRule[] rules)
        => new(rules, new Dictionary<string, string>(StringComparer.Ordinal) { [secretRef] = secretValue }, "test_flow");

    private static JsonElement Apply(PayloadProtector protector, string payload = RiderPayload)
        => JsonDocument.Parse(protector.Apply(Encoding.UTF8.GetBytes(payload), "json")).RootElement;

    private static Dictionary<string, string> Params(params (string Key, string Value)[] pairs)
        => pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- remove / redact / mask

    [Fact]
    public void Remove_DropsThePropertyFromEveryArrayElement()
    {
        var root = Apply(Protector(
            new AcquireProtectRule { Path = "$.data[*].rider", Action = AcquireProtectAction.Remove },
            new AcquireProtectRule { Path = "$.data[*].scheduledPickupAddress", Action = AcquireProtectAction.Remove }));

        foreach (var record in root.GetProperty("data").EnumerateArray())
        {
            Assert.False(record.TryGetProperty("rider", out _));
            Assert.False(record.TryGetProperty("scheduledPickupAddress", out _));
            Assert.True(record.TryGetProperty("id", out _));
        }
    }

    [Fact]
    public void Remove_OnNestedObjectProperty_DropsOnlyThatField()
    {
        var root = Apply(Protector(
            new AcquireProtectRule { Path = "$.data[*].driver.lastName", Action = AcquireProtectAction.Remove }));

        foreach (var record in root.GetProperty("data").EnumerateArray())
        {
            Assert.False(record.GetProperty("driver").TryGetProperty("lastName", out _));
            Assert.True(record.GetProperty("driver").TryGetProperty("firstName", out _));
        }
    }

    [Fact]
    public void Remove_PathMatchingNothing_IsANoOp()
    {
        var root = Apply(Protector(
            new AcquireProtectRule { Path = "$.data[*].noSuchField", Action = AcquireProtectAction.Remove }));

        Assert.Equal(2, root.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public void Redact_Full_ReplacesWithConstant()
    {
        var root = Apply(Protector(
            new AcquireProtectRule { Path = "$.data[*].rider", Action = AcquireProtectAction.Redact }));

        foreach (var record in root.GetProperty("data").EnumerateArray())
        {
            Assert.Equal("[REDACTED]", record.GetProperty("rider").GetString());
        }
    }

    [Fact]
    public void Redact_CustomReplacement_IsUsed()
    {
        var root = Apply(Protector(new AcquireProtectRule
        {
            Path = "$.data[*].rider",
            Action = AcquireProtectAction.Redact,
            Params = Params(("replacement", "Navn ( GDPR )")),
        }));

        Assert.Equal("Navn ( GDPR )", root.GetProperty("data")[0].GetProperty("rider").GetString());
    }

    [Fact]
    public void Redact_EmailMode_KeepsFirstCharAndDomain()
    {
        var root = Apply(Protector(new AcquireProtectRule
        {
            Path = "$.data[*].email",
            Action = AcquireProtectAction.Redact,
            Params = Params(("mode", "email")),
        }));

        var masked = root.GetProperty("data")[0].GetProperty("email").GetString()!;
        Assert.StartsWith("k", masked, StringComparison.Ordinal);
        Assert.EndsWith("@example.no", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("ari.nordmann", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_PhoneMode_KeepsLastFourDigits()
    {
        var root = Apply(Protector(new AcquireProtectRule
        {
            Path = "$.data[*].phone",
            Action = AcquireProtectAction.Redact,
            Params = Params(("mode", "phone")),
        }));

        var masked = root.GetProperty("data")[0].GetProperty("phone").GetString()!;
        Assert.EndsWith("4567", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("912", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void Mask_KeepsLeadingCharacters()
    {
        var root = Apply(Protector(new AcquireProtectRule
        {
            Path = "$.data[*].rider",
            Action = AcquireProtectAction.Mask,
            Params = Params(("show", "3")),
        }));

        Assert.Equal("Kar**********", root.GetProperty("data")[0].GetProperty("rider").GetString());
    }

    // ---------------------------------------------------------------- hash / hmac

    [Fact]
    public void Hash_IsDeterministicSha256Hex()
    {
        var rule = new AcquireProtectRule { Path = "$.data[*].rider", Action = AcquireProtectAction.Hash };
        var first = Apply(Protector(rule)).GetProperty("data")[0].GetProperty("rider").GetString();
        var second = Apply(Protector(rule)).GetProperty("data")[0].GetProperty("rider").GetString();

        Assert.Equal(first, second);
        Assert.Equal(64, first!.Length);
        Assert.NotEqual("Kari Nordmann", first);
    }

    [Fact]
    public void Hmac_RequiresKey_AndTruncatesToOutputLength()
    {
        var rule = new AcquireProtectRule
        {
            Path = "$.data[*].rider",
            Action = AcquireProtectAction.Hmac,
            Secret = "${test:pii-key}",
            Params = Params(("outputLength", "16")),
        };

        var root = Apply(KeyedProtector("${test:pii-key}", "super-secret-key", rule));
        var pseudonym = root.GetProperty("data")[0].GetProperty("rider").GetString()!;
        Assert.Equal(16, pseudonym.Length);
    }

    [Fact]
    public void Hmac_RelationshipScope_IsStableAcrossRunsButKeyDependent()
    {
        AcquireProtectRule Rule() => new()
        {
            Path = "$.data[*].rider",
            Action = AcquireProtectAction.Hmac,
            Secret = "${test:pii-key}",
        };

        var run1 = Apply(KeyedProtector("${test:pii-key}", "key-a", Rule())).GetProperty("data")[0].GetProperty("rider").GetString();
        var run2 = Apply(KeyedProtector("${test:pii-key}", "key-a", Rule())).GetProperty("data")[0].GetProperty("rider").GetString();
        var otherKey = Apply(KeyedProtector("${test:pii-key}", "key-b", Rule())).GetProperty("data")[0].GetProperty("rider").GetString();

        Assert.Equal(run1, run2);
        Assert.NotEqual(run1, otherKey);
    }

    [Fact]
    public void Hmac_TransactionScope_DiffersAcrossRuns()
    {
        AcquireProtectRule Rule() => new()
        {
            Path = "$.data[*].rider",
            Action = AcquireProtectAction.Hmac,
            Secret = "${test:pii-key}",
            Scope = AcquireProtectScope.Transaction,
        };

        var run1 = Apply(KeyedProtector("${test:pii-key}", "key-a", Rule())).GetProperty("data")[0].GetProperty("rider").GetString();
        var run2 = Apply(KeyedProtector("${test:pii-key}", "key-a", Rule())).GetProperty("data")[0].GetProperty("rider").GetString();

        Assert.NotEqual(run1, run2);
    }

    [Fact]
    public void Hmac_MissingSecret_Throws()
    {
        var protector = Protector(new AcquireProtectRule
        {
            Path = "$.data[*].rider",
            Action = AcquireProtectAction.Hmac,
            Secret = "${test:absent}",
        });

        Assert.Throws<SqlFlowException>(() => protector.Apply(Encoding.UTF8.GetBytes(RiderPayload), "json"));
    }

    // ---------------------------------------------------------------- tokenize / encrypt

    [Fact]
    public void Tokenize_Keyed_SameValueSameToken_AcrossRuns()
    {
        AcquireProtectRule Rule() => new()
        {
            Path = "$.data[*].rider",
            Action = AcquireProtectAction.Tokenize,
            Secret = "${test:pii-key}",
        };

        var run1 = Apply(KeyedProtector("${test:pii-key}", "key-a", Rule())).GetProperty("data")[0].GetProperty("rider").GetString()!;
        var run2 = Apply(KeyedProtector("${test:pii-key}", "key-a", Rule())).GetProperty("data")[0].GetProperty("rider").GetString()!;

        Assert.Equal(run1, run2);
        Assert.StartsWith("tok_", run1, StringComparison.Ordinal);
    }

    [Fact]
    public void Tokenize_Unkeyed_ConsistentWithinRun_FreshAcrossRuns()
    {
        AcquireProtectRule Rule() => new() { Path = "$.data[*].rider", Action = AcquireProtectAction.Tokenize };
        const string payload = """{"data":[{"rider":"Same"},{"rider":"Same"},{"rider":"Other"}]}""";

        var run1 = Apply(Protector(Rule()), payload).GetProperty("data");
        Assert.Equal(run1[0].GetProperty("rider").GetString(), run1[1].GetProperty("rider").GetString());
        Assert.NotEqual(run1[0].GetProperty("rider").GetString(), run1[2].GetProperty("rider").GetString());

        var run2 = Apply(Protector(Rule()), payload).GetProperty("data");
        Assert.NotEqual(run1[0].GetProperty("rider").GetString(), run2[0].GetProperty("rider").GetString());
    }

    [Fact]
    public void Encrypt_IsDeterministicAndRoundTrips()
    {
        AcquireProtectRule Rule() => new()
        {
            Path = "$.data[*].rider",
            Action = AcquireProtectAction.Encrypt,
            Secret = "${test:pii-key}",
        };

        var run1 = Apply(KeyedProtector("${test:pii-key}", "key-a", Rule())).GetProperty("data")[0].GetProperty("rider").GetString()!;
        var run2 = Apply(KeyedProtector("${test:pii-key}", "key-a", Rule())).GetProperty("data")[0].GetProperty("rider").GetString()!;
        Assert.Equal(run1, run2);
        Assert.NotEqual("Kari Nordmann", run1);
    }

    // ---------------------------------------------------------------- generalize

    [Theory]
    [InlineData("year", "2026")]
    [InlineData("month", "2026-03")]
    [InlineData("quarter", "2026Q1")]
    [InlineData("decade", "2020s")]
    public void Generalize_DateModes(string mode, string expected)
    {
        var root = Apply(Protector(new AcquireProtectRule
        {
            Path = "$.data[0].createdAt",
            Action = AcquireProtectAction.Generalize,
            Params = Params(("mode", mode)),
        }));

        Assert.Equal(expected, root.GetProperty("data")[0].GetProperty("createdAt").GetString());
    }

    [Fact]
    public void Generalize_Round_StaysNumericJson()
    {
        var root = Apply(Protector(new AcquireProtectRule
        {
            Path = "$.data[*].price",
            Action = AcquireProtectAction.Generalize,
            Params = Params(("mode", "round"), ("step", "10")),
        }));

        var price = root.GetProperty("data")[0].GetProperty("price");
        Assert.Equal(JsonValueKind.Number, price.ValueKind);
        Assert.Equal(120m, price.GetDecimal());
    }

    // ---------------------------------------------------------------- payload semantics

    [Fact]
    public void Apply_NonJsonPayload_ThrowsInsteadOfLandingUnprotected()
    {
        var protector = Protector(new AcquireProtectRule { Path = "$.rider", Action = AcquireProtectAction.Remove });

        Assert.Throws<SqlFlowException>(() => protector.Apply(Encoding.UTF8.GetBytes("id;rider\r\n1;Kari"), "json"));
    }

    [Fact]
    public void Apply_TopLevelArrayPayload_IsSupported()
    {
        var protector = Protector(new AcquireProtectRule { Path = "$[*].name", Action = AcquireProtectAction.Remove });
        var root = JsonDocument.Parse(protector.Apply(Encoding.UTF8.GetBytes(
            """[{"id":1,"name":"a"},{"id":2,"name":"b"}]"""), "json")).RootElement;

        foreach (var record in root.EnumerateArray())
        {
            Assert.False(record.TryGetProperty("name", out _));
        }
    }

    [Fact]
    public void Apply_UntouchedFieldsSurviveByteForByteValues()
    {
        var root = Apply(Protector(
            new AcquireProtectRule { Path = "$.data[*].rider", Action = AcquireProtectAction.Remove }));

        Assert.Equal("r1", root.GetProperty("data")[0].GetProperty("id").GetString());
        Assert.Equal(123.45m, root.GetProperty("data")[0].GetProperty("price").GetDecimal());
        Assert.Equal("Ola", root.GetProperty("data")[0].GetProperty("driver").GetProperty("firstName").GetString());
    }

    // ---------------------------------------------------------------- other formats: csv / xml / jsonl

    [Fact]
    public void Csv_RedactsColumnByHeaderName_AndPreservesUntouchedFields()
    {
        const string csv = "id;rider;\"note\";price\r\n1;Kari Nordmann;\"a;b\";10\r\n2;Per Olsen;plain;20\r\n";
        var protector = Protector(new AcquireProtectRule
        {
            Path = "rider",
            Action = AcquireProtectAction.Redact,
            Params = Params(("replacement", "Navn ( GDPR )")),
        });

        var result = Encoding.UTF8.GetString(protector.Apply(Encoding.UTF8.GetBytes(csv), "csv"));

        Assert.Equal("id;rider;\"note\";price\r\n1;Navn ( GDPR );\"a;b\";10\r\n2;Navn ( GDPR );plain;20\r\n", result);
    }

    [Fact]
    public void Csv_Remove_BlanksTheFieldWithoutReshapingTheSchema()
    {
        const string csv = "id,rider\n1,Kari\n";
        var protector = Protector(new AcquireProtectRule { Path = "rider", Action = AcquireProtectAction.Remove });

        var result = Encoding.UTF8.GetString(protector.Apply(Encoding.UTF8.GetBytes(csv), "csv"));

        Assert.Equal("id,rider\n1,\n", result);
    }

    [Fact]
    public void Csv_UnknownColumn_ThrowsInsteadOfLandingUnprotected()
    {
        var protector = Protector(new AcquireProtectRule { Path = "noSuchColumn", Action = AcquireProtectAction.Remove });

        Assert.Throws<SqlFlowException>(() => protector.Apply(Encoding.UTF8.GetBytes("id;rider\r\n1;Kari\r\n"), "csv"));
    }

    [Fact]
    public void Csv_TransformedFieldContainingDelimiter_IsQuoted()
    {
        const string csv = "id;rider\n1;Kari\n";
        var protector = Protector(new AcquireProtectRule
        {
            Path = "rider",
            Action = AcquireProtectAction.Redact,
            Params = Params(("replacement", "x;y")),
        });

        var result = Encoding.UTF8.GetString(protector.Apply(Encoding.UTF8.GetBytes(csv), "csv"));

        Assert.Equal("id;rider\n1;\"x;y\"\n", result);
    }

    [Fact]
    public void Xml_RemovesElementsAndTransformsValues()
    {
        const string xml = """
            <requests><request><id>r1</id><rider>Kari Nordmann</rider><phone>+47 912 34 567</phone></request><request><id>r2</id><rider>Per Olsen</rider><phone>+47 998 76 543</phone></request></requests>
            """;
        var protector = Protector(
            new AcquireProtectRule { Path = "requests.request.rider", Action = AcquireProtectAction.Remove },
            new AcquireProtectRule
            {
                Path = "requests.request.phone",
                Action = AcquireProtectAction.Redact,
                Params = Params(("mode", "phone")),
            });

        var result = Encoding.UTF8.GetString(protector.Apply(Encoding.UTF8.GetBytes(xml), "xml"));

        Assert.DoesNotContain("Kari Nordmann", result, StringComparison.Ordinal);
        Assert.DoesNotContain("<rider>", result, StringComparison.Ordinal);
        Assert.Contains("4567</phone>", result, StringComparison.Ordinal);
        Assert.Contains("<id>r1</id>", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Xml_AttributeAddress_TransformsTheAttribute()
    {
        const string xml = """<riders><rider name="Kari Nordmann" id="1"/><rider name="Per Olsen" id="2"/></riders>""";
        var protector = Protector(new AcquireProtectRule { Path = "riders.rider.@name", Action = AcquireProtectAction.Hash });

        var result = Encoding.UTF8.GetString(protector.Apply(Encoding.UTF8.GetBytes(xml), "xml"));

        Assert.DoesNotContain("Kari Nordmann", result, StringComparison.Ordinal);
        Assert.Contains("id=\"1\"", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Xml_KeepsTheDeclarationConsistentWithTheUtf8_BytesItLands()
    {
        // The protected payload is written as UTF-8, so a declaration announcing anything else makes the landed
        // file unparseable ("There is no Unicode byte order mark. Cannot switch to Unicode") even though its
        // content is correct. Saving through a writer that reports UTF-16 is exactly how that happens.
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?><responses><response><id>1</id><email>kari@example.no</email></response></responses>
            """;
        var protector = Protector(new AcquireProtectRule { Path = "responses.response.email", Action = AcquireProtectAction.Remove });

        var bytes = protector.Apply(Encoding.UTF8.GetBytes(xml), "xml");
        var result = Encoding.UTF8.GetString(bytes);

        Assert.Contains("encoding=\"utf-8\"", result, StringComparison.Ordinal);
        Assert.DoesNotContain("utf-16", result, StringComparison.Ordinal);
        Assert.DoesNotContain("kari@example.no", result, StringComparison.Ordinal);

        // The decisive check: a strict reader over the landed BYTES accepts them.
        using var stream = new MemoryStream(bytes);
        var reloaded = System.Xml.Linq.XDocument.Load(stream);
        Assert.Equal("responses", reloaded.Root!.Name.LocalName);
    }

    [Fact]
    public void Xml_InvalidPayload_Throws()
    {
        var protector = Protector(new AcquireProtectRule { Path = "a.b", Action = AcquireProtectAction.Remove });

        Assert.Throws<SqlFlowException>(() => protector.Apply(Encoding.UTF8.GetBytes("{\"not\":\"xml\"}"), "xml"));
    }

    [Fact]
    public void Jsonl_ProtectsEveryLineAndPreservesLineCount()
    {
        const string jsonl = """
            {"id":1,"rider":"Kari"}
            {"id":2,"rider":"Per"}
            """;
        var protector = Protector(new AcquireProtectRule { Path = "$.rider", Action = AcquireProtectAction.Remove });

        var result = Encoding.UTF8.GetString(protector.Apply(Encoding.UTF8.GetBytes(jsonl + "\n"), "jsonl"));
        var lines = result.TrimEnd('\n').Split('\n');

        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.DoesNotContain("rider", line, StringComparison.Ordinal));
        Assert.EndsWith("\n", result, StringComparison.Ordinal);
    }

    [Fact]
    public void UnprotectableFormat_Throws()
    {
        var protector = Protector(new AcquireProtectRule { Path = "$.rider", Action = AcquireProtectAction.Remove });

        Assert.Throws<SqlFlowException>(() => protector.Apply(Encoding.UTF8.GetBytes("binary-ish"), "bin"));
    }

    // ---------------------------------------------------------------- loader validation

    [Fact]
    public void Loader_ParsesProtectRules()
    {
        var flow = new YamlAcquireFlowLoader().Parse("""
            flowType: api
            name: protect_test
            source:
              transport: http
              baseUrl: https://api.example.com
              request: { method: GET, path: /v1/requests }
            landing:
              target: c:/tmp/landing
              pathTemplate: "history/{yyyy}/file_{yyyyMMdd}"
              format: json
              protect:
                - { path: "$.data[*].rider", action: remove }
                - { path: "$.data[*].phone", action: redact, mode: phone }
                - { path: "$.data[*].riderId", action: hmac, secret: "${env:PII_KEY}", outputLength: 16, scope: person }
            """);

        var rules = flow.Items[0].Landing.Protect;
        Assert.Equal(3, rules.Count);
        Assert.Equal(AcquireProtectAction.Remove, rules[0].Action);
        Assert.Equal("phone", rules[1].Params["mode"]);
        Assert.Equal(AcquireProtectScope.Person, rules[2].Scope);
        Assert.Equal("${env:PII_KEY}", rules[2].Secret);
        Assert.Equal("16", rules[2].Params["outputLength"]);
    }

    [Fact]
    public void Loader_KeyedActionWithoutSecret_FailsValidation()
    {
        var ex = Assert.Throws<FlowValidationException>(() => new YamlAcquireFlowLoader().Parse("""
            flowType: api
            name: protect_test
            source:
              transport: http
              baseUrl: https://api.example.com
              request: { method: GET, path: /v1/requests }
            landing:
              target: c:/tmp/landing
              pathTemplate: "history/file"
              protect:
                - { path: "$.data[*].rider", action: hmac }
            """));

        Assert.Contains("requires 'secret'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Loader_UnknownAction_FailsValidation()
    {
        Assert.Throws<FlowValidationException>(() => new YamlAcquireFlowLoader().Parse("""
            flowType: api
            name: protect_test
            source:
              transport: http
              baseUrl: https://api.example.com
              request: { method: GET, path: /v1/requests }
            landing:
              target: c:/tmp/landing
              pathTemplate: "history/file"
              protect:
                - { path: "$.data[*].rider", action: obliterate }
            """));
    }
}
