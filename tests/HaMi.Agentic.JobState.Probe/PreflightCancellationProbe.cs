// Temporary isolated negative fixture for HaMi AI Engineering #26.
// Never execute this method. Remove the fixture after CA2016 is observed.
internal static class PreflightCancellationProbe
{
    internal static Task DelayAsync(CancellationToken cancellationToken) => Task.Delay(1);
}
