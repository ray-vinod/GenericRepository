namespace GenericRepository.Console.Entities;

public class Product : Auditable
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public decimal Price { get; set; }
    public int Stock { get; set; }

    public int CategoryId { get; set; }
    public Category? Category { get; set; }
}

