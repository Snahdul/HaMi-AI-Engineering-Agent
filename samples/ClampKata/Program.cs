// Trusted, fixed test harness. Model output must never modify this file.
// Input contract: min <= max. The correct reference is Math.Clamp(value, min, max).
var cases = new (int Value, int Min, int Max)[]
{
    (-10, -5, 5), (-5, -5, 5), (0, -5, 5), (5, -5, 5), (10, -5, 5),
    (int.MinValue, -50, -3), (int.MaxValue, 3, 100), (15, 0, 10),
    (-3, 0, 10), (10, 10, 10), (int.MinValue, int.MinValue, int.MaxValue),
    (int.MaxValue, int.MinValue, int.MaxValue), (20, -100, 100), (10, 0, 9)
};

var random = new Random(1337);
var failed = 0;
var tested = 0;
foreach (var (value, min, max) in cases)
{
    Check(value, min, max);
}

for (int i = 0; i < 128; i++)
{
    int a = random.Next(-50000, 50001);
    int b = random.Next(-50000, 50001);
    int min = Math.Min(a, b);
    int max = Math.Max(a, b);
    Check(random.Next(-100000, 100001), min, max);
}

Console.WriteLine($"ClampKata: {tested} tests, {failed} failures.");
return failed == 0 ? 0 : 1;

void Check(int value, int min, int max)
{
    tested++;
    int expected = Math.Clamp(value, min, max);
    try
    {
        int actual = ClampKata.Clamp(value, min, max);
        if (actual != expected)
        {
            failed++;
            if (failed <= 8)
                Console.Error.WriteLine($"FAIL value={value} min={min} max={max}: expected {expected}, got {actual}");
        }
    }
    catch (Exception ex)
    {
        failed++;
        if (failed <= 8)
            Console.Error.WriteLine($"FAIL value={value} min={min} max={max}: {ex.GetType().Name}");
    }
}
