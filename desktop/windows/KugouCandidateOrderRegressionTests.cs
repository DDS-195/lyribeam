using System.Reflection;
namespace LyricBridge;

internal static class KugouCandidateOrderRegressionTests
{
    private sealed record Candidate(string Id, double Score, double Difference, string Group);
    public static Task<Dictionary<string, bool>> RunAsync(string root)
    {
        var checks = new Dictionary<string, bool>();
        var repeated = Enumerable.Range(1,12).Select(i=>new Candidate("duplicate-family-"+i,105,34,"same-title\n228127")).ToList();
        repeated.Add(new("usable-thirteenth",105,93,"same-title\n228000"));
        repeated.AddRange(Enumerable.Range(1,7).Select(i=>new Candidate("other-family-"+i,105,142,"explicit-title\n228235")));
        var ordered=Order(repeated);
        checks["A candidate originally after twelve lookalikes is tried in the first group round"] = ordered.FindIndex(c=>c.Id=="usable-thirteenth") == 1;
        checks["A failed first eight cannot permanently hide later trustworthy candidates"] = ordered.Count==20 && ordered.Any(c=>c.Id=="usable-thirteenth");
        checks["The first round samples distinct title-duration groups before repeated variants"] = ordered.Take(3).Select(c=>c.Group).Distinct().Count()==3;
        checks["Repeated variants remain available after representative candidates fail"] = ordered.Count(c=>c.Group=="same-title\n228127")==12 && ordered.Last().Id=="duplicate-family-12";

        var scores=new[]{new Candidate("lower-close",90,0,"low"),new Candidate("higher-second",105,9,"high-b"),new Candidate("higher-first",105,2,"high-a"),new Candidate("higher-third",105,8,"high-a")};
        ordered=Order(scores);
        checks["A lower score cannot run before all candidates in the stronger score tier"] = ordered.Take(3).All(c=>c.Score==105) && ordered.Last().Id=="lower-close";
        checks["The closest duration starts the round within an equal score tier"] = ordered.First().Id=="higher-first";

        ordered=Order(new[]{new Candidate("same-id",90,0,"weak"),new Candidate("other-id",105,3,"other"),new Candidate("same-id",105,1,"strong")});
        checks["Repeated identifiers are downloaded once using their strongest ranked evidence"] = ordered.Count==2 && ordered.Single(c=>c.Id=="same-id").Score==105;
        checks["A large service response retains a bounded twenty-candidate budget"] = Order(Enumerable.Range(0,80).Select(i=>new Candidate("id-"+i,105,i,"group-"+i))).Count==20;
        checks["An empty service result remains empty"] = Order(Array.Empty<Candidate>()).Count==0;
        checks["A single title-duration family still retains all twenty bounded attempts"] = Order(Enumerable.Range(0,20).Select(i=>new Candidate("id-"+i,105,i,"one-group"))).Count==20;
        return Task.FromResult(checks);
    }
    private static List<Candidate> Order(IEnumerable<Candidate> candidates)
    {
        // The fallback permits this exact suite to demonstrate old-release failures.
        var type=typeof(KugouCandidateOrderRegressionTests).Assembly.GetType("LyricBridge.KugouCandidateOrder")
            ?? typeof(MediaSnapshot).Assembly.GetType("LyricBridge.KugouCandidateOrder");
        var method=type?.GetMethod("Interleave",BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public);
        if(method==null) return candidates.OrderByDescending(c=>c.Score).ThenBy(c=>c.Difference).DistinctBy(c=>c.Id).Take(8).ToList();
        return (List<Candidate>)method.MakeGenericMethod(typeof(Candidate)).Invoke(null,new object[]{candidates,
            (Func<Candidate,string>)(c=>c.Id),(Func<Candidate,double>)(c=>c.Score),(Func<Candidate,double>)(c=>c.Difference),(Func<Candidate,string>)(c=>c.Group)})!;
    }
}
