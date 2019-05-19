namespace NuSave.Tests
{
  using System;
  using Core;
  using Microsoft.VisualStudio.TestTools.UnitTesting;

  [TestClass]
  public class UnitTests
  {
    [TestMethod]
    public void TestDependencyResolver()
    {
      var downloader = new Downloader(null, Environment.CurrentDirectory, "System.Collections", "4.3.0");
      downloader.ResolveDependencies();
      var deps = downloader.GetDependencies();
      Assert.AreEqual(deps.Count, 4);
    }
  }
}