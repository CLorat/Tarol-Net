using Tarol.Results;
static class R5 {

  public static Result<object> B(object o) => o;
  public static Result<object> C(Violation v) => v;
}
