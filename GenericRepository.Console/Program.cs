using GenericRepository;
using Microsoft.EntityFrameworkCore;


// A manual, run-it-and-read-the-output exercise of GenericRepository.EFCore against a real
// (SQLite) database, using a Category 1-to-many Product relationship. Each run starts from a
// clean database file placed next to the built binaries (already covered by the repo's
// .gitignore via bin/, so nothing here needs its own ignore rule).

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
        Console.WriteLine($"DatabaseExistsAsync() -> {await uow.DatabaseExistsAsync()}");

        Section("Add categories and products");

        var electronics = new Category { Name = "Electronics" };
        var books = new Category { Name = "Books" };

        await uow.Of<Category>().AddAsync(electronics);
        await uow.Of<Category>().AddAsync(books);
        await uow.SaveChangesAsync();

        Console.WriteLine($"Electronics.Id = {electronics.Id}, CreatedAt = {electronics.CreatedAt:O}");
        Console.WriteLine($"Books.Id = {books.Id}, CreatedAt = {books.CreatedAt:O}");

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

        Section("GetAllAsync (default: soft-deleted excluded)");

        var allProducts = await uow.Of<Product>().GetAllAsync();
        foreach (var p in allProducts!)
        {
            Console.WriteLine($"  {p.Name} - stock {p.Stock}");
        }

        Section("GetAllAsync with a predicate + eager-loaded Category");

        var electronicsProducts = await uow.Of<Product>().GetAllAsync(
            p => p.CategoryId == electronics.Id,
            includes: [p => p.Category!]);

        foreach (var p in electronicsProducts!)
        {
            Console.WriteLine($"  {p.Name} is in category '{p.Category?.Name}'");
        }

        Section("FindAsync a single product");

        var found = await uow.Of<Product>().FindAsync(p => p.Name == "Laptop");
        Console.WriteLine($"Found: {found?.Name}, price {found?.Price:C}");

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
        Console.WriteLine($"Persisted: Name = {afterMerge?.Name}, Price = {afterMerge?.Price:C}, UpdatedAt = {afterMerge?.UpdatedAt:O}");
        Console.WriteLine($"No duplicate-tracking exception — original tracked instance now reads Name = {laptop.Name}");

        Section("Soft delete and restore");

        var wasSoftDeleted = await uow.Of<Product>().SoftDeleteAsync(headphones);
        await uow.SaveChangesAsync();
        Console.WriteLine($"SoftDeleteAsync(headphones) returned {wasSoftDeleted}, DeletedAt = {headphones.DeletedAt:O}");

        var repeatSoftDelete = await uow.Of<Product>().SoftDeleteAsync(headphones);
        Console.WriteLine($"Calling it again returns {repeatSoftDelete} (already deleted — no-op, original DeletedAt preserved)");

        var afterSoftDelete = await uow.Of<Product>().GetAllAsync();
        Console.WriteLine($"GetAllAsync() count after soft-deleting headphones: {afterSoftDelete!.Count()} (headphones excluded)");

        var byId = await uow.Of<Product>().GetByIdAsync(headphones.Id);
        Console.WriteLine($"GetByIdAsync(id) now returns: {(byId is null ? "null — soft-deleted rows are always excluded" : byId.Name)}");

        var softDeleted = await uow.Of<Product>().GetSoftDeletedAsync();
        Console.WriteLine($"GetSoftDeletedAsync() shows: {string.Join(", ", softDeleted!.Select(p => p.Name))}");

        var wasRestored = await uow.Of<Product>().RestoreAsync(headphones);
        await uow.SaveChangesAsync();
        Console.WriteLine($"RestoreAsync(headphones) returned {wasRestored}, DeletedAt = {headphones.DeletedAt:O}");

        var afterRestore = await uow.Of<Product>().GetAllAsync();
        Console.WriteLine($"GetAllAsync() count after restoring headphones: {afterRestore!.Count()}");

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

        Section("Transaction commit");

        await using (var transaction = await uow.BeginTransactionAsync())
        {
            var clearance = new Category { Name = "Clearance" };
            await uow.Of<Category>().AddAsync(clearance);
            await uow.SaveChangesAsync();
            await transaction.CommitAsync();

            Console.WriteLine($"Committed new category '{clearance.Name}' (Id = {clearance.Id})");
        }

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

        var countAfterRollback = (await uow.Of<Category>().GetAllAsync())!.Count();
        Console.WriteLine($"Category count before: {countBeforeRollback}, after rollback: {countAfterRollback} (should match)");

        Section("Hard delete");

        // CategoryId -> Category is configured with DeleteBehavior.Restrict, so Products
        // referencing 'books' must be removed first, or SaveChangesAsync would throw.
        await uow.Of<Product>().DeleteAsync(novel.Id);
        await uow.SaveChangesAsync();
        await uow.Of<Category>().DeleteAsync(books.Id);
        await uow.SaveChangesAsync();

        var remainingCategories = await uow.Of<Category>().GetAllAsync();
        Console.WriteLine($"Categories remaining: {string.Join(", ", remainingCategories!.Select(c => c.Name))}");

        Console.WriteLine();
        Console.WriteLine("Done.");

        static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine($"--- {title} ---");
        }

        Console.ReadLine();
    }
}