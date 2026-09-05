namespace GenericRepository.Console.Entities;

public class Category : Auditable
{
    public int Id { get; set; }
    public required string Name { get; set; }

    public ICollection<Product> Products { get; set; } = [];
}
