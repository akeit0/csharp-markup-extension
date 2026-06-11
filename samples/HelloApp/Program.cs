using Csmx.Samples.HelloApp.Components;

var direct = Counter.Render("World!!", 3);
Console.WriteLine(direct.ToMarkup());
