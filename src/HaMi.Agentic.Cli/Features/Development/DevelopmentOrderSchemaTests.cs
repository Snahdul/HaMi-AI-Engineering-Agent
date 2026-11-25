using System.Text.Json;

namespace HaMi.Agentic.Cli.Features.Development;

internal static class DevelopmentOrderSchemaTests
{
    internal static void SelfTest()
    {
        const string prefix = """{"$schema":"https://json-schema.org/draft/2020-12/schema",""";
        DevelopmentOrderSchema Parse(string body) => DevelopmentOrderSchema.Parse(prefix + body[1..]);

        void Check(string body, string instance, bool expected)
        {
            using JsonDocument document = JsonDocument.Parse(instance);
            if (Parse(body).Validate(document.RootElement) != expected)
                throw new InvalidOperationException("Order schema evaluation regression.");
        }

        void RejectSchema(string body)
        {
            try
            {
                _ = Parse(body);
            }
            catch (InvalidOperationException)
            {
                return;
            }

            throw new InvalidOperationException("Unsupported order schema accepted.");
        }

        // Independent cases based on Draft 2020-12 keyword semantics:
        // https://github.com/json-schema-org/JSON-Schema-Test-Suite/tree/main/tests/draft2020-12
        const string objectSchema = """{"type":"object","required":["a"],"properties":{"a":{"type":"integer"}},"additionalProperties":false}""";
        Check(objectSchema, """{"a":1}""", true);
        Check(objectSchema, """{"a":null}""", false);
        Check(objectSchema, """{"a":1,"b":2}""", false);
        Check(objectSchema, """{"A":1}""", false);
        Check(objectSchema, """{"a":1,"a":1}""", false);
        Check(objectSchema, "[]", false);
        Check(objectSchema, "{}", false);

        const string numberSchema = """{"type":"integer","minimum":1,"maximum":3}""";
        Check(numberSchema, "1", true);
        Check(numberSchema, "3", true);
        Check(numberSchema, "0", false);
        Check(numberSchema, "4", false);
        Check(numberSchema, "1.0", false); // Strict HaMi Int32 representation.
        Check(numberSchema, "2147483648", false);

        const string textSchema = """{"type":"string","minLength":1,"maxLength":1}""";
        Check(textSchema, "\"😀\"", true);
        Check(textSchema, "\"ab\"", false);
        Check(textSchema, "\"\"", false);
        Check("""{"type":"string","pattern":"^S[0-9]{2}$"}""", "\"S01\"", true);
        Check("""{"type":"string","pattern":"^S[0-9]{2}$"}""", "\"s01\"", false);
        Check("""{"const":1}""", "1", true);
        Check("""{"const":1}""", "\"1\"", false);
        Check("""{"enum":["baseline","step"]}""", "\"step\"", true);
        Check("""{"enum":["baseline","step"]}""", "\"other\"", false);

        const string arraySchema = """{"type":"array","minItems":1,"maxItems":2,"items":{"type":"string"},"uniqueItems":true}""";
        Check(arraySchema, """["a","b"]""", true);
        Check(arraySchema, """["a","a"]""", false);
        Check(arraySchema, """["a",1]""", false);
        Check(arraySchema, "[]", false);
        Check(arraySchema, """["a","b","c"]""", false);
        Check("""{"uniqueItems":true}""", """[{"a":1,"b":2},{"b":2,"a":1}]""", false);
        Check("""{"uniqueItems":true}""", """[{"a":1},{"a":2}]""", true);
        Check("""{"uniqueItems":true}""", """[["a"],["\u0061"]]""", false);

        const string anyOf = """{"anyOf":[{"type":"string","pattern":"^S[0-9]{2}$"},{"type":"null"}]}""";
        Check(anyOf, "null", true);
        Check(anyOf, "\"S02\"", true);
        Check(anyOf, "true", false);
        Check(anyOf, "\"invalid\"", false);

        RejectSchema("""{"type":"object","$ref":"remote"}""");
        RejectSchema("""{"anyOf":[{"type":"null"},{"unknown":true}]}""");
        RejectSchema("""{"properties":{"a":{"minLength":-1}}}""");
        RejectSchema("""{"properties":{"a":true}}""");
        RejectSchema("""{"type":"number"}""");
        RejectSchema("""{"type":"object","type":"array"}""");
        RejectSchema("""{"pattern":".*"}""");
        RejectSchema("""{"required":["a","a"]}""");
        RejectSchema("""{"minimum":2,"maximum":1}""");
        RejectSchema("""{"anyOf":[]}""");
        Console.WriteLine("Development order schema subset self-test passed.");
    }
}
