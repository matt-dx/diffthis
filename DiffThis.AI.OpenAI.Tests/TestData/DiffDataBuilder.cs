using DiffThis.Core.Models;

namespace DiffThis.AI.OpenAI.Tests.TestData;

/// <summary>
/// Builds synthetic <see cref="DiffResult"/> objects for testing.
/// No git subprocess — all data is hard-coded or generated inline.
/// </summary>
public static class DiffDataBuilder
{
    // ── Small diff ────────────────────────────────────────────────────────
    // 2 files, ~40 lines changed  ≈ 1 500 chars of diff content

    public static DiffResult SmallDiff() => new()
    {
        RepositoryPath   = @"C:\projects\sample-api",
        RepositoryName   = "sample-api",
        BaseBranch       = "main",
        CompareBranch    = "feature/email-lookup",
        Files            =
        [
            MakeFile("src/Repositories/UserRepository.cs", DiffFileStatus.Modified,
                oldStart: 42, newStart: 42,
                context: "GetByIdAsync",
                additions:
                [
                    "    public async Task<User?> GetByEmailAsync(string email, CancellationToken ct = default)",
                    "    {",
                    "        return await _db.Users",
                    "            .AsNoTracking()",
                    "            .FirstOrDefaultAsync(u => u.Email == email, ct);",
                    "    }",
                ],
                deletions: []),
            MakeFile("src/Services/AuthService.cs", DiffFileStatus.Modified,
                oldStart: 88, newStart: 88,
                context: "ValidateTokenAsync",
                additions:
                [
                    "        if (string.IsNullOrWhiteSpace(token))",
                    "            return AuthResult.Fail(\"Token is missing\");",
                    "",
                    "        var payload = _jwt.Decode(token);",
                    "        if (payload is null || payload.ExpiresAt < DateTimeOffset.UtcNow)",
                    "            return AuthResult.Fail(\"Token is expired or invalid\");",
                    "",
                    "        var user = await _users.GetByEmailAsync(payload.Email, ct);",
                    "        if (user is null)",
                    "            return AuthResult.Fail(\"User not found\");",
                    "",
                    "        return AuthResult.Ok(user);",
                ],
                deletions:
                [
                    "        throw new NotImplementedException();",
                ]),
        ],
    };

    // ── Medium diff ───────────────────────────────────────────────────────
    // 5 files, ~400 lines changed  ≈ 15 000 chars of diff content

    public static DiffResult MediumDiff() => new()
    {
        RepositoryPath = @"C:\projects\sample-api",
        RepositoryName = "sample-api",
        BaseBranch     = "main",
        CompareBranch  = "feature/order-service-refactor",
        Files          =
        [
            MakeFile("src/Models/Order.cs", DiffFileStatus.Modified,
                oldStart: 1, newStart: 1, context: "Order",
                additions: GenerateLines("+", "        public ", new[]
                {
                    "Guid Id { get; init; } = Guid.NewGuid();",
                    "string CustomerId { get; set; } = string.Empty;",
                    "List<OrderLine> Lines { get; set; } = [];",
                    "OrderStatus Status { get; set; } = OrderStatus.Pending;",
                    "DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;",
                    "DateTimeOffset? ShippedAt { get; set; }",
                    "decimal SubTotal => Lines.Sum(l => l.Quantity * l.UnitPrice);",
                    "decimal Tax => SubTotal * 0.1m;",
                    "decimal Total => SubTotal + Tax;",
                    "string ShippingAddress { get; set; } = string.Empty;",
                    "string BillingAddress { get; set; } = string.Empty;",
                    "string? CouponCode { get; set; }",
                    "decimal Discount { get; set; }",
                    "string Notes { get; set; } = string.Empty;",
                    "bool IsDeleted { get; set; }",
                    "Guid? AssignedWarehouseId { get; set; }",
                    "int Priority { get; set; } = 5;",
                    "Dictionary<string, string> Metadata { get; set; } = [];",
                    "string TrackingNumber { get; set; } = string.Empty;",
                    "List<OrderEvent> Events { get; set; } = [];",
                }, count: 30),
                deletions: GenerateLines("-", "        public ", new[]
                {
                    "int Id { get; set; }",
                    "string Customer { get; set; } = string.Empty;",
                    "string Status { get; set; } = \"Pending\";",
                }, count: 10)),

            MakeFile("src/Services/OrderService.cs", DiffFileStatus.Modified,
                oldStart: 15, newStart: 15, context: "PlaceOrderAsync",
                additions: RepeatLines(80, "            // process order line, validate stock, reserve inventory"),
                deletions: RepeatLines(60, "            // TODO: implement")),

            MakeFile("src/Controllers/OrdersController.cs", DiffFileStatus.Modified,
                oldStart: 30, newStart: 30, context: "Post",
                additions: RepeatLines(60, "            var result = await _orders.PlaceOrderAsync(request, ct);"),
                deletions: RepeatLines(40, "            throw new NotImplementedException();")),

            MakeFile("src/Repositories/OrderRepository.cs", DiffFileStatus.Added,
                oldStart: 0, newStart: 1, context: "OrderRepository",
                additions: RepeatLines(100, "        await _db.SaveChangesAsync(ct);"),
                deletions: []),

            MakeFile("tests/OrderServiceTests.cs", DiffFileStatus.Added,
                oldStart: 0, newStart: 1, context: "PlaceOrder_ValidRequest_CreatesOrder",
                additions: RepeatLines(80, "        Assert.Equal(OrderStatus.Pending, order.Status);"),
                deletions: []),
        ],
    };

    // ── Large-medium diff (boundary probe) ───────────────────────────────
    // ~28 000 chars — sits between known-pass (~15k) and known-fail (~60k).
    // Used to narrow down the actual GitHub Models per-request token limit.

    public static DiffResult LargeMediumDiff() => new()
    {
        RepositoryPath = @"C:\projects\sample-api",
        RepositoryName = "sample-api",
        BaseBranch     = "main",
        CompareBranch  = "feature/large-medium-probe",
        Files          = BuildFiles(fileCount: 8, linesPerFile: 60),
    };

    // ── Large diff ────────────────────────────────────────────────────────
    // 12 files, ~2 000 lines changed  ≈ 75 000 chars of diff content.
    // Designed to exceed the ~8 k-token per-request limit seen on small models,
    // and to stress-test GPT-4o's handling of the current single-message approach.

    public static DiffResult LargeDiff() => new()
    {
        RepositoryPath = @"C:\projects\enterprise-platform",
        RepositoryName = "enterprise-platform",
        BaseBranch     = "main",
        CompareBranch  = "feature/v3-api-migration",
        Files          = BuildLargeFiles(),
    };

    /// <summary>Public overload — lets integration tests build arbitrary-sized diffs for probing.</summary>
    public static DiffResult BuildFilesPublic(int fileCount, int linesPerFile) => new()
    {
        RepositoryPath = @"C:\projects\sample-api",
        RepositoryName = "sample-api",
        BaseBranch     = "main",
        CompareBranch  = $"feature/probe-{fileCount}f-{linesPerFile}l",
        Files          = BuildFiles(fileCount, linesPerFile),
    };

    /// <summary>Generates <paramref name="fileCount"/> modified files each with
    /// <paramref name="linesPerFile"/> added lines of realistic-looking C# code.</summary>
    private static List<DiffFile> BuildFiles(int fileCount, int linesPerFile)
    {
        var files = new List<DiffFile>();
        for (int f = 0; f < fileCount; f++)
        {
            var adds = new List<string>();
            for (int i = 0; i < linesPerFile; i++)
                adds.Add($"            var result{i} = await _repository.GetByIdAsync(id{i}, cancellationToken);");

            var dels = new List<string>();
            for (int i = 0; i < linesPerFile / 3; i++)
                dels.Add($"            // TODO: implement method {i}");

            files.Add(MakeFile($"src/Services/Service{f}.cs", DiffFileStatus.Modified,
                oldStart: 1, newStart: 1, context: "ProcessAsync",
                additions: adds.ToArray(), deletions: dels.ToArray()));
        }
        return files;
    }

    private static List<DiffFile> BuildLargeFiles()
    {
        var files = new List<DiffFile>();

        // Simulate a large migration: 12 service/repo/controller files each with ~160 lines changed
        string[] fileNames =
        [
            "src/Services/CustomerService.cs",
            "src/Services/ProductService.cs",
            "src/Services/InvoiceService.cs",
            "src/Services/PaymentService.cs",
            "src/Services/ShipmentService.cs",
            "src/Repositories/CustomerRepository.cs",
            "src/Repositories/ProductRepository.cs",
            "src/Repositories/InvoiceRepository.cs",
            "src/Controllers/CustomersController.cs",
            "src/Controllers/ProductsController.cs",
            "src/Controllers/InvoicesController.cs",
            "src/Controllers/PaymentsController.cs",
        ];

        int lineNum = 1;
        foreach (var name in fileNames)
        {
            // Mix of method bodies that look realistic at ~50 chars per line
            var addLines = new List<string>();
            for (int i = 0; i < 100; i++)
                addLines.Add($"            var entity{i} = await _repository.GetByIdAsync(id{i}, cancellationToken);");
            for (int i = 0; i < 40; i++)
                addLines.Add($"            _logger.LogInformation(\"Processing entity {{Id}}\", entity{i}?.Id);");
            for (int i = 0; i < 20; i++)
                addLines.Add($"            await _cache.SetAsync($\"entity-{{id{i}}}\", entity{i}, _cacheOptions, cancellationToken);");

            var delLines = new List<string>();
            for (int i = 0; i < 80; i++)
                delLines.Add($"            // TODO: implement v3 API for entity {i}");
            for (int i = 0; i < 30; i++)
                delLines.Add($"            throw new NotImplementedException(\"Method not yet migrated to v3\");");

            files.Add(MakeFile(name, DiffFileStatus.Modified,
                oldStart: lineNum, newStart: lineNum,
                context: "ProcessAsync",
                additions: addLines.ToArray(),
                deletions: delLines.ToArray()));

            lineNum += addLines.Count;
        }

        return files;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static DiffFile MakeFile(
        string path,
        DiffFileStatus status,
        int oldStart,
        int newStart,
        string context,
        string[] additions,
        string[] deletions)
    {
        var lines = new List<DiffLine>();

        foreach (var a in additions)
            lines.Add(new DiffLine { Type = DiffLineType.Addition, Content = a });

        foreach (var d in deletions)
            lines.Add(new DiffLine { Type = DiffLineType.Deletion, Content = d });

        return new DiffFile
        {
            OldPath    = path,
            NewPath    = path,
            Status     = status,
            Additions  = additions.Length,
            Deletions  = deletions.Length,
            Hunks      =
            [
                new DiffHunk
                {
                    OldStart = oldStart,
                    OldCount = deletions.Length,
                    NewStart = newStart,
                    NewCount = additions.Length,
                    Context  = context,
                    Lines    = lines,
                },
            ],
        };
    }

    private static string[] GenerateLines(string prefix, string memberPrefix, string[] templates, int count)
    {
        var result = new List<string>();
        for (int i = 0; i < count; i++)
            result.Add(memberPrefix + templates[i % templates.Length]);
        return result.ToArray();
    }

    private static string[] RepeatLines(int count, string line)
    {
        var result = new string[count];
        Array.Fill(result, line);
        return result;
    }
}
