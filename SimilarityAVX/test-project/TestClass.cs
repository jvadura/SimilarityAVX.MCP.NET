using System;

namespace TestProject
{
    public class TestClass
    {
        public string Name { get; set; }
        
        public void PrintMessage()
        {
            Console.WriteLine("Hello from TestClass!");
        }
        
        public void NewMethod()
        {
            Console.WriteLine("This is a new method added to test reindexing!");
        }
    }
}