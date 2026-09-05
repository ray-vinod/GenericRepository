using GenericRepository;
using Microsoft.EntityFrameworkCore;


// A run-it-and-verify exercise of GenericRepository.EFCore against a real (SQLite) database,
// using a Category 1-to-many Product relationship. Each run starts from a clean database file
// placed next to the built binaries (already covered by the repo's .gitignore via bin/, so
// nothing here needs its own ignore rule).
//
// This doubles as the project's CI smoke test (see .github/workflows/publish-nuget.yml) — there
// is no separate unit test project, so every checkpoint below throws via Assert() on a wrong
// result, not just on an unhandled exception, and the process exits non-zero if anything fails.

using GenericRepository.Console;
using GenericRepository.Console.Entities;

internal class Program
{
    private static async Task Main(string[] args)
    {
        var dbPath = Path.Combine(AppContext.BaseDirectory, "sandbox.db");
        if (File.Exists(dbPath))
        {
            File.Delete(dbPath);
        }

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        var context = new AppDbContext(options);
        await context.Database.EnsureCreatedAsync();

        using IUnitOfWork uow = new UnitOfWork<AppDbContext>(context);

        Section("Database connectivity");
        var dbExists = await uow.DatabaseExistsAsync();
        Console.WriteLine($"DatabaseExistsAsync() -> {dbExists}");
        Assert(dbExists, "DatabaseExistsAsync() should be true right after EnsureCreatedAsync().");

        Section("Add categories and products");

        var electronics = new Category { Name = "Electronics" };
        var books = new Category { Name = "Books" };

        await uow.Of<Category>().AddAsync(electronics);
        await uow.Of<Category>().AddAsync(books);
        await uow.SaveChangesAsync();

        Console.WriteLine($"Electronics.Id = {electronics.Id}, CreatedAt = {electronics.CreatedAt:O}");
        Console.WriteLine($"Books.Id = {books.Id}, CreatedAt = {books.CreatedAt:O}");
        Assert(electronics.Id > 0 && books.Id > 0, "Both categories should have auto-generated ids after SaveChangesAsync().");
        Assert(electronics.CreatedAt != default && electronics.UpdatedAt != default, "CreatedAt/UpdatedAt should be stamped on insert.");
        Assert(electronics.DeletedAt is null, "A freshly-added category should not be soft-deleted.");

        var laptop = new Product
        {
            Name = "Laptop",
            Price = 1200m,
            Stock = 5,
            CategoryId = electronics.Id
        };

        var headphones = new Product
        {
            Name = "Headphones",
            Price = 80m,
            Stock = 20,
            CategoryId = electronics.Id
        };
        var novel = new Product
        {
            Name = "Novel",
            Price = 15m,
            Stock = 50,
            CategoryId = books.Id
        };

        await uow.Of<Product>().AddRangeAsync([laptop, headphones, novel]);
        await uow.SaveChangesAsync();

        Console.WriteLine($"Added {laptop.Name}, {headphones.Name}, {novel.Name} (auto-generated ids: {laptop.Id}, {headphones.Id}, {novel.Id})");
        Assert(laptop.Id > 0 && headphones.Id > 0 && novel.Id > 0, "All three products should have auto-generated ids.");
        var originalLaptopCreatedAt = laptop.CreatedAt;

        Section("GetAllAsync (default: soft-deleted excluded)");

        var allProducts = (await uow.Of<Product>().GetAllAsync())!.ToList();
        foreach (var p in allProducts)
        {
            Console.WriteLine($"  {p.Name} - stock {p.Stock}");
        }
        Assert(allProducts.Count == 3, $"Expected 3 products, got {allProducts.Count}.");

        Section("GetAllAsync with a predicate + eager-loaded Category");

        var electronicsProducts = (await uow.Of<Product>().GetAllAsync(
            p => p.CategoryId == electronics.Id,
            includes: [p => p.Category!])).ToList();

        foreach (var p in electronicsProducts)
        {
            Console.WriteLine($"  {p.Name} is in category '{p.Category?.Name}'");
        }
        Assert(electronicsProducts.Count == 2, $"Expected 2 electronics products, got {electronicsProducts.Count}.");
        Assert(electronicsProducts.All(p => p.Category?.Name == "Electronics"), "Eager-loaded Category should be populated and correct for every result.");

        Section("FindAsync a single product");

        var found = await uow.Of<Product>().FindAsync(p => p.Name == "Laptop");
        Console.WriteLine($"Found: {found?.Name}, price {found?.Price:C}");
        Assert(found is not null && found.Price == 1200m, "FindAsync should locate the laptop at its original price.");

        Section("Smart UpdateAsync: merging a detached instance into an already-tracked one");

        // 'laptop' is still tracked (Unchanged) from the AddAsync/SaveChangesAsync above. Build a
        // *separate*, detached instance with the same key but different values, simulating a fresh
        // object deserialized from a request body — this is exactly the scenario UpdateAsync's
        // tracked-local-merge path exists for. Without that path, this would throw
        // "the instance of entity type 'Product' cannot be tracked because another instance with
        // the same key value is already being tracked".
        var incomingLaptop = new Product
        {
            Id = laptop.Id,
            Name = "Laptop Pro",
            Price = 1500m,
            Stock = laptop.Stock,
            CategoryId = electronics.Id,
        };

        await uow.Of<Product>().UpdateAsync(incomingLaptop);
        await uow.SaveChangesAsync();

        var afterMerge = await uow.Of<Product>().GetByIdAsync(laptop.Id);
        Console.WriteLine($"Persisted: Name = {afterMerge?.Name}, Price = {afterMerge?.Price:C}, CreatedAt = {afterMerge?.CreatedAt:O}, UpdatedAt = {afterMerge?.UpdatedAt:O}");
        Console.WriteLine($"No duplicate-tracking exception — original tracked instance now reads Name = {laptop.Name}");

        Assert(afterMerge is not null, "The merged laptop should still exist.");
        Assert(afterMerge!.Name == "Laptop Pro" && afterMerge.Price == 1500m, "The incoming values should have been applied.");
        Assert(afterMerge.CreatedAt == originalLaptopCreatedAt, "CreatedAt must survive a merge update untouched — this is the exact NOT NULL bug this app caught earlier.");
        Assert(afterMerge.UpdatedAt > originalLaptopCreatedAt, "UpdatedAt should be re-stamped by the merge update.");

        Section("Soft delete and restore");

        var wasSoftDeleted = await uow.Of<Product>().SoftDeleteAsync(headphones);
        await uow.SaveChangesAsync();
        Console.WriteLine($"SoftDeleteAsync(headphones) returned {wasSoftDeleted}, DeletedAt = {headphones.DeletedAt:O}");
        Assert(wasSoftDeleted, "SoftDeleteAsync should succeed on an active entity.");
        Assert(headphones.DeletedAt is not null, "DeletedAt should be stamped after a soft delete.");
        var originalDeletedAt = headphones.DeletedAt;

        var repeatSoftDelete = await uow.Of<Product>().SoftDeleteAsync(headphones);
        Console.WriteLine($"Calling it again returns {repeatSoftDelete} (already deleted — no-op, original DeletedAt preserved)");
        Assert(!repeatSoftDelete, "SoftDeleteAsync should return false and change nothing when called twice.");
        Assert(headphones.DeletedAt == originalDeletedAt, "A repeat SoftDeleteAsync call must not overwrite the original DeletedAt.");

        var afterSoftDelete = (await uow.Of<Product>().GetAllAsync())!.ToList();
        Console.WriteLine($"GetAllAsync() count after soft-deleting headphones: {afterSoftDelete.Count} (headphones excluded)");
        Assert(afterSoftDelete.Count == 2 && afterSoftDelete.All(p => p.Name != "Headphones"), "GetAllAsync() should exclude the soft-deleted headphones.");

        var byId = await uow.Of<Product>().GetByIdAsync(headphones.Id);
        Console.WriteLine($"GetByIdAsync(id) now returns: {(byId is null ? "null — soft-deleted rows are always excluded" : byId.Name)}");
        Assert(byId is null, "GetByIdAsync() should never return a soft-deleted row.");

        var softDeleted = (await uow.Of<Product>().GetSoftDeletedAsync())!.ToList();
        Console.WriteLine($"GetSoftDeletedAsync() shows: {string.Join(", ", softDeleted.Select(p => p.Name))}");
        Assert(softDeleted.Count == 1 && softDeleted[0].Name == "Headphones", "GetSoftDeletedAsync() should show exactly the soft-deleted headphones.");

        var wasRestored = await uow.Of<Product>().RestoreAsync(headphones);
        await uow.SaveChangesAsync();
        Console.WriteLine($"RestoreAsync(headphones) returned {wasRestored}, DeletedAt = {headphones.DeletedAt:O}");
        Assert(wasRestored, "RestoreAsync should succeed on a soft-deleted entity.");
        Assert(headphones.DeletedAt is null, "DeletedAt should be cleared after a restore.");

        var afterRestore = (await uow.Of<Product>().GetAllAsync())!.ToList();
        Console.WriteLine($"GetAllAsync() count after restoring headphones: {afterRestore.Count}");
        Assert(afterRestore.Count == 3, "All three products should be visible again after the restore.");

        Section("Paging");

        var page1 = await uow.Of<Product>().GetPagedAsync(
            pageNumber: 1,
            pageSize: 2,
            orderBy: q => q.OrderBy(p => p.Name));

        Console.WriteLine($"Page {page1.PageNumber}/{page1.TotalPages} (total {page1.TotalItemCount} items):");
        foreach (var p in page1.Items)
        {
            Console.WriteLine($"  {p.Name}");
        }
        Console.WriteLine($"HasNextPage = {page1.HasNextPage}, HasPreviousPage = {page1.HasPreviousPage}");

        Assert(page1.TotalItemCount == 3, "Paging should see all 3 active products.");
        Assert(page1.Items.Count() == 2, "Page size 2 should return 2 items.");
        Assert(page1.TotalPages == 2, "3 items at page size 2 should be 2 pages.");
        Assert(page1.HasNextPage && !page1.HasPreviousPage, "Page 1 of 2 should have a next page but no previous page.");

        Section("Transaction commit");

        await using (var transaction = await uow.BeginTransactionAsync())
        {
            var clearance = new Category { Name = "Clearance" };
            await uow.Of<Category>().AddAsync(clearance);
            await uow.SaveChangesAsync();
            await transaction.CommitAsync();

            Console.WriteLine($"Committed new category '{clearance.Name}' (Id = {clearance.Id})");
            Assert(clearance.Id > 0, "The committed category should have a generated id.");
        }

        var categoriesAfterCommit = (await uow.Of<Category>().GetAllAsync())!.ToList();
        Assert(categoriesAfterCommit.Any(c => c.Name == "Clearance"), "The committed category should be visible after commit.");

        Section("Transaction rollback");

        var countBeforeRollback = (await uow.Of<Category>().GetAllAsync())!.Count();

        await using (var transaction = await uow.BeginTransactionAsync())
        {
            await uow.Of<Category>().AddAsync(new Category { Name = "Should not persist" });
            await uow.SaveChangesAsync();
            await transaction.RollbackAsync();
            // Note for later: RollbackAsync() undoes the database write, but the ChangeTracker
            // still believes that Category was saved (SaveChangesAsync marked it Unchanged before
            // the rollback happened). GetAllAsync() below re-queries the database directly so it
            // reports reality correctly — but GetByIdAsync() trusts the local tracked cache first,
            // so calling it for that specific id here could return the "ghost" entity. In real
            // code, clear or recreate the context after a manual rollback if you'll keep using it.
        }

        var categoriesAfterRollback = (await uow.Of<Category>().GetAllAsync())!.ToList();
        var countAfterRollback = categoriesAfterRollback.Count;
        Console.WriteLine($"Category count before: {countBeforeRollback}, after rollback: {countAfterRollback} (should match)");
        Assert(countAfterRollback == countBeforeRollback, "A rolled-back transaction must not persist.");
        Assert(categoriesAfterRollback.All(c => c.Name != "Should not persist"), "The rolled-back category must not be visible.");

        Section("Hard delete");

        // CategoryId -> Category is configured with DeleteBehavior.Restrict, so Products
        // referencing 'books' must be removed first, or SaveChangesAsync would throw.
        await uow.Of<Product>().DeleteAsync(novel.Id);
        await uow.SaveChangesAsync();
        await uow.Of<Category>().DeleteAsync(books.Id);
        await uow.SaveChangesAsync();

        var remainingCategories = (await uow.Of<Category>().GetAllAsync())!.ToList();
        Console.WriteLine($"Categories remaining: {string.Join(", ", remainingCategories.Select(c => c.Name))}");
        Assert(remainingCategories.All(c => c.Name != "Books"), "The hard-deleted Books category should be gone.");
        Assert(remainingCategories.Any(c => c.Name == "Electronics") && remainingCategories.Any(c => c.Name == "Clearance"), "Electronics and Clearance should still be present.");

        Console.WriteLine();
        Console.WriteLine("All checks passed.");

        if (!Console.IsInputRedirected)
        {
            Console.WriteLine();
            Console.WriteLine("Press Enter to exit...");
            Console.ReadLine();
        }

        static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine($"--- {title} ---");
        }

        static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException($"Verification failed: {message}");
            }
        }
    }
}
