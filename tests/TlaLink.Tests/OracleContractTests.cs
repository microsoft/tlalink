using System.Buffers;
using System.Text;
using System.Text.Json;
using TlaLink;

namespace TlaLink.Tests;

public static class OracleContractTests
{
    public static void Register(Action<string, Action> test)
    {
        JsonSerializerOptions compact = new(IntegratedInputs.Json) { WriteIndented = false };
        test("oracle scalars retain Int32 and Boolean types without coercion", () =>
        {
            foreach (int number in new[] { int.MinValue, -1, 0, 1, int.MaxValue })
            {
                OracleInputScalar input = new(number);
                Require(input.Integer == number && input.Boolean == null && !input.IsBoolean);
            }
            foreach (bool boolean in new[] { false, true })
            {
                OracleInputScalar input = new(boolean);
                Require(input.Boolean == boolean && input.Integer == null && input.IsBoolean);
            }
            Require(JsonSerializer.Serialize(default(OracleInputScalar), compact) == "0");
        });
        test("oracle scalar wire tokens round trip including authored negative zero", () =>
        {
            foreach (string json in new[] { "-2147483648", "-1", "-0", "0", "1", "2147483647", "true", "false" })
            {
                OracleInputScalar input = JsonSerializer.Deserialize<OracleInputScalar>(json, compact);
                Require(JsonSerializer.Serialize(input, compact) == json);
            }
        });
        test("oracle scalars reject non-Int32 numeric tokens and non-Boolean JSON values", () =>
        {
            foreach (string json in new[] { "null", "\"1\"", "\"true\"", "1.0", "1e0", "-0.0",
                "2147483648", "-2147483649", "9223372036854775807", "[]", "{}" })
            {
                try
                {
                    JsonSerializer.Deserialize<OracleInputScalar>(json, compact);
                    throw new InvalidOperationException("Unsupported oracle input was accepted: " + json);
                }
                catch (InputException error)
                {
                    Require(error.Code == ErrorCode.InvalidOracleInput);
                }
            }
        });
        test("oracle case input arrays preserve the existing scalar JSON shape", () =>
        {
            const string json = """{"id":"one","inputs":[1,true,-0,false,-2147483648,2147483647]}""";
            ConformanceCase input = JsonSerializer.Deserialize<ConformanceCase>(json, compact)!;
            Require(input.Inputs.Count == 6 && input.Inputs[0].Integer == 1 && input.Inputs[1].Boolean == true);
            Require(JsonSerializer.Serialize(input, compact) == json);
        });
        test("oracle negative zero survives segmented JSON input", () =>
        {
            Segment first = new(Encoding.UTF8.GetBytes("-"));
            Segment last = first.Append(Encoding.UTF8.GetBytes("0"));
            Utf8JsonReader reader = new(new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length));
            OracleInputScalar input = JsonSerializer.Deserialize<OracleInputScalar>(ref reader, compact);
            Require(input.Integer == 0 && JsonSerializer.Serialize(input, compact) == "-0");
        });
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Oracle scalar contract assertion failed.");
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(byte[] bytes) => Memory = bytes;

        public Segment Append(byte[] bytes)
        {
            Segment next = new(bytes) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
