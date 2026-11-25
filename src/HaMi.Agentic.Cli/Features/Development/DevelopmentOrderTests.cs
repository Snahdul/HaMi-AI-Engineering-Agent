using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HaMi.Agentic.Cli.Features.Development;

internal static class DevelopmentOrderTests
{
    internal static void SelfTest(string repo)
    {
        string plan = File.ReadAllText(Path.Combine(repo, "samples/DevelopmentOrder/pair-order.json"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!plan.EndsWith('\n'))
            throw new InvalidOperationException("Reference order must end with LF.");

        string Body(string json) => "### Approved plan\n\n```hami-order-json\n" + json + "```\n";
        DevelopmentOrder Parse(string body) => DevelopmentOrder.ParseIssueBody(Encoding.UTF8.GetBytes(body));
        DevelopmentOrder order = Parse(Body(plan));
        if (order.Plan != plan || order.Sha256 != DevelopmentJobState.Hash(Encoding.UTF8.GetBytes(plan))
            || Parse(Body(plan).Replace("\n", "\r\n", StringComparison.Ordinal)).Sha256 != order.Sha256
            || Parse("Discussion\n" + Body(plan) + "\nProgress").Sha256 != order.Sha256
            || Parse(Body(" " + plan)).Sha256 == order.Sha256)
            throw new InvalidOperationException("Order exact-byte identity regression.");

        void Reject(string body) => ExpectRejected(() => Parse(body));

        void RejectMutation(Action<JsonObject> mutate)
        {
            JsonObject node = JsonNode.Parse(plan)!.AsObject();
            mutate(node);
            Reject(Body(node.ToJsonString() + "\n"));
        }

        Reject(plan);
        Reject(Body(plan) + Body(plan));
        Reject(Body(plan).Replace("```\n", "", StringComparison.Ordinal));
        Reject(Body(plan).Replace("\n", "\r", StringComparison.Ordinal));
        Reject(Body(plan.Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 1, \"schemaVersion\": 1,", StringComparison.Ordinal)));
        Reject(Body(plan.Replace("\"source\": \"baseline\",", "\"source\": \"baseline\", \"source\": \"baseline\",", StringComparison.Ordinal)));
        ExpectRejected(() => DevelopmentOrder.ParseIssueBody([0xff]));
        ExpectRejected(() => DevelopmentOrder.ParseIssueBody([0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(Body(plan))]));
        Reject(new string('x', 64 * 1024) + Body(plan));
        Reject(Body(new string(' ', 32 * 1024) + plan));
        RejectMutation(x => x["schemaVersion"] = 2);
        RejectMutation(x => x["unknown"] = true);
        RejectMutation(x => x.Remove("goal"));
        RejectMutation(x => x["goal"] = " ");
        RejectMutation(x => x["goal"] = "bad\u0000text");
        RejectMutation(x => x["repository"] = "other/repo");
        RejectMutation(x => x["baseCommit"] = "not-a-commit");
        RejectMutation(x => x["steps"]![1]!["unknown"] = 1);
        RejectMutation(x => x["steps"]![1]!["id"] = "S01");
        RejectMutation(x => x["steps"]![0]!["dependsOn"] = new JsonArray("S02"));
        RejectMutation(x => x["steps"]![1]!["dependsOn"] = new JsonArray("S99"));
        RejectMutation(x => x["steps"]![1]!["inputs"]![1]!["id"] = "missing-output");
        RejectMutation(x => x["steps"]![1]!["inputs"]![1]!["stepId"] = "S02");
        RejectMutation(x => x["steps"]![0]!["competencies"] = new JsonArray("shell"));
        RejectMutation(x => x["steps"]![1]!["allowedWritePaths"] = new JsonArray(DevelopmentAgentContract.PairFirst));
        RejectMutation(x => x["steps"]![0]!["allowedWritePaths"] = new JsonArray("src/HaMi.Agentic.Cli/../Outside.cs"));
        RejectMutation(x => x["steps"]![1]!["verifier"] = "model-claims-success");
        RejectMutation(x => x["finalVerification"]!["verifier"] = "skip");
        RejectMutation(x => x["steps"]![1]!["criteria"]![0]!["id"] = "C01");
        RejectMutation(x => x["limits"]!["modelCalls"] = 3);
        RejectMutation(x => x["limits"]!["sandboxRuns"] = 4);
        RejectMutation(x => x["limits"]!["elapsedSeconds"] = 1801);
        Console.WriteLine("Development order schema, scope, dependencies, budgets and exact-byte identity self-test passed.");
    }

    private static void ExpectRejected(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException)
        {
            return;
        }

        throw new InvalidOperationException("Invalid development order was accepted.");
    }
}
