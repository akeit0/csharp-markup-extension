using Csmx.Samples.FactoryApp.Components;

var node = FactoryCounter.Render("Factory!!", 7);
Console.WriteLine(node.ToMarkup());
