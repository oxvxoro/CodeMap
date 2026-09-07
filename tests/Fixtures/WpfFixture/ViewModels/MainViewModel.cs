namespace WpfFixture.ViewModels;

/// <summary>Plain CLR object — no WPF/MVVM base class required, since XamlMarkupAnalyzer
/// resolution only needs a source class, never an ICommand implementation (plan §3.5).</summary>
public sealed class MainViewModel
{
    public string CustomerName { get; set; } = string.Empty;

    public Customer Customer { get; set; } = new();

    public void Save()
    {
        CustomerName = CustomerName.Trim();
    }

    // Plain property standing in for an ICommand — plan §3.5 does not require an actual
    // ICommand implementation, only a single matching public property or method.
    public object? SaveCommand { get; set; }
}

public sealed class Customer
{
    public string Name { get; set; } = string.Empty;
}
