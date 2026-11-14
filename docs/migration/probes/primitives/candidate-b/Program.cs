var g = Eval.B.Gallery.Open("x"); var a = g.Hang("a"); a.Reprice(3);
Console.WriteLine($"b: version={g.Version} price={Eval.B.Gallery.Rehydrate(g.Id, g.UncommittedChanges.ToArray()).Artworks[0].Price}");
