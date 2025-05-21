using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

public class ProductsPageModel : PageModel
{
    private readonly AppDbContext _context;

    public ProductsPageModel(AppDbContext context)
    {
        _context = context;
    }

    [BindProperty]
    public ProductManagement NewProduct { get; set; }

    public List<ProductManagement> Products { get; set; }

    public void OnGet()
    {
        Products = _context.Products.OrderByDescending(p => p.Date).ToList();
    }

    public IActionResult OnPost()
    {
        if (!ModelState.IsValid)
        {
            Products = _context.Products.OrderByDescending(p => p.Date).ToList();
            return Page();
        }

        NewProduct.Date = DateTime.UtcNow;
        _context.Products.Add(NewProduct);
        _context.SaveChanges();

        return RedirectToPage();
    }

    private readonly IHttpClientFactory _clientFactory;

    public ProductsPageModel(IHttpClientFactory clientFactory)
    {
        _clientFactory = clientFactory;
    }
    public async Task OnGetAsync()
    {
        var token = HttpContext.Session.GetString("JWT");
        if (string.IsNullOrEmpty(token)) return;

        var client = _clientFactory.CreateClient("ApiClient");
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetFromJsonAsync<List<ProductManagement>>("/products");
        Products = response ?? new();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var token = HttpContext.Session.GetString("JWT");
        if (string.IsNullOrEmpty(token)) return RedirectToPage("/Login");

        var client = _clientFactory.CreateClient("ApiClient");
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/products", NewProduct);

        if (response.IsSuccessStatusCode)
            return RedirectToPage();

        var error = await response.Content.ReadAsStringAsync();
        ModelState.AddModelError(string.Empty, error);
        return Page();
    }
}