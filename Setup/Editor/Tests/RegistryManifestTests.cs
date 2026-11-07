using System;
using NUnit.Framework;

namespace Polysmith.Setup.Tests
{
    public sealed class RegistryManifestTests
    {
        [Test]
        public void AddsOnlyTheRequiredScopeAndIsIdempotent()
        {
            const string json = "{\"dependencies\":{\"example\":\"file:../example\"},\"custom\":42}";
            string result = RegistryManifest.Configure(json);
            Assert.That(result, Does.Contain("file:../example"));
            Assert.That(result, Does.Contain("\"custom\": 42"));
            Assert.That(result, Does.Contain(RegistryManifest.Package));
            Assert.That(RegistryManifest.Configure(result), Is.EqualTo(result));
        }

        [Test]
        public void PreservesOtherRegistriesAndExtendsExistingOpenUpm()
        {
            string json = "{\"scopedRegistries\":[{\"name\":\"other\",\"url\":\"https://other\",\"scopes\":[\"com.other\"]}," +
                "{\"name\":\"mine\",\"url\":\"https://package.openupm.com/\",\"scopes\":[\"com.mine\"]}]}";
            string result = RegistryManifest.Configure(json);
            Assert.That(result, Does.Contain("com.other"));
            Assert.That(result, Does.Contain("com.mine"));
            Assert.That(result.Split(new[] { "https://package.openupm.com" }, StringSplitOptions.None).Length, Is.EqualTo(2));
        }

        [Test]
        public void RejectsConflictingScopesWithoutChangingTheInput()
        {
            const string json = "{\"scopedRegistries\":[{\"url\":\"https://other\",\"scopes\":[\"org.khronos\"]}]}";
            Assert.Throws<InvalidOperationException>(() => RegistryManifest.Configure(json));
        }
    }
}
