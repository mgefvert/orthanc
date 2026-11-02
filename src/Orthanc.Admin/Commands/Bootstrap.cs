using DotNetCommons.Commands;
using Orthanc.Core;
using Orthanc.Core.Entities;

namespace Orthanc.Admin.Commands;

[CommandAction(["bootstrap"], "Create a customer and its first PSK in an existing schema", [])]
public sealed class Bootstrap : AdminCommand
{
    public override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        await using var db = OpenContext();

        var c = ConfigureCustomerFromArgs(new DbCustomer());

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.Customers.Add(c);
        await db.SaveChangesAsync(ct);

        // Commit before displaying the secret, so an announced key always exists.
        var secret = Credentials.Generate();

        var key = new DbApiKey
        {
            CustomerId = c.Id,
            Label = Args.Label,
            KeyHash = Credentials.Hash(secret)
        };

        if (Args.Label.Length > 200)
            throw new ArgumentException("Label too long");

        db.ApiKeys.Add(key);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        Console.WriteLine($"Customer ID: {c.Id}\nKey ID: {key.Id}\nPSK (shown once): {secret}");
        return 0;
    }
}