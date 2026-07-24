namespace MiniProject;

public class Greeter
{
    private readonly Calculator _calculator = new();

    public string SayHello(string name) => $"Hello, {name}!";

    public string GreetWithSum(string name, int a, int b)
    {
        int sum = _calculator.Add(a, b);
        return $"{SayHello(name)} The sum of {a} and {b} is {sum}.";
    }

    public static void Main(string[] args)
    {
        var greeter = new Greeter();
        System.Console.WriteLine(greeter.GreetWithSum("World", 2, 3));
    }
}
