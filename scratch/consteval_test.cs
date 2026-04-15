using System;

class Program
{
    consteval static int Add(int a, int b)
    {
        if (a < b)
        {
            return b * a;
        }
        else
        {
            return a + b;
        }
    }

    static void Main()
    {
        const int a = 10;
        int x = Add(a, 30);
        Console.WriteLine(x);
    }
}
