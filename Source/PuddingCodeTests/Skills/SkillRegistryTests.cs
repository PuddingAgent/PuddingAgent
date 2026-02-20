namespace PuddingCodeTests.Skills;

using PuddingCode.Skills;
using PuddingCode.Models;

[TestClass]
public sealed class SkillRegistryTests
{
    private SkillRegistry _registry = null!;

    [TestInitialize]
    public void Setup()
    {
        _registry = new SkillRegistry();
    }

    // ──── Registration Tests ────

    [TestMethod]
    public void Register_DiscoversPuddingSkillAnnotatedMethods()
    {
        // Arrange
        var skillInstance = new TestSkills();

        // Act
        _registry.Register(skillInstance);

        // Assert
        var skills = _registry.GetAllSkills();
        Assert.AreEqual(5, skills.Count);
        Assert.IsTrue(skills.Any(s => s.Name == "test_skill"));
        Assert.IsTrue(skills.Any(s => s.Name == "another_skill"));
    }

    [TestMethod]
    public void Register_ConvertsMethodNameToSnakeCase()
    {
        // Arrange
        var skillInstance = new TestSkills();

        // Act
        _registry.Register(skillInstance);

        // Assert
        var skill = _registry.FindSkill("test_skill");
        Assert.IsNotNull(skill);
        Assert.AreEqual("TestSkill", skill!.Method.Name);
    }

    [TestMethod]
    public void Register_StoresSkillMetadata()
    {
        // Arrange
        var skillInstance = new TestSkills();

        // Act
        _registry.Register(skillInstance);

        // Assert
        var skill = _registry.FindSkill("test_skill");
        Assert.IsNotNull(skill);
        Assert.AreEqual("A test skill", skill!.Description);
        Assert.AreEqual("Test", skill.Group);
    }

    [TestMethod]
    public void Register_IncludesAllowedRoles()
    {
        // Arrange
        var skillInstance = new TestSkills();

        // Act
        _registry.Register(skillInstance);

        // Assert
        var skill = _registry.FindSkill("restricted_skill");
        Assert.IsNotNull(skill);
        Assert.AreEqual(1, skill!.AllowedRoles.Length);
        Assert.AreEqual(AgentRole.Leader, skill.AllowedRoles[0]);
    }

    // ──── Parameter Schema Tests ────

    [TestMethod]
    public void Register_GeneratesParameterSchema()
    {
        // Arrange
        var skillInstance = new TestSkills();

        // Act
        _registry.Register(skillInstance);

        // Assert
        var skill = _registry.FindSkill("skill_with_params");
        Assert.IsNotNull(skill);
        Assert.AreEqual(2, skill!.Parameters.Properties.Count);
        CollectionAssert.Contains(
            skill.Parameters.Properties.Select(p => p.Name).ToList(),
            "message");
        CollectionAssert.Contains(
            skill.Parameters.Properties.Select(p => p.Name).ToList(),
            "count");
    }

    [TestMethod]
    public void Register_MapsClrTypesToJsonTypes()
    {
        // Arrange
        var skillInstance = new TestSkills();

        // Act
        _registry.Register(skillInstance);

        // Assert
        var skill = _registry.FindSkill("skill_with_params");
        var props = skill!.Parameters.Properties.ToDictionary(p => p.Name, p => p.Type);
        
        Assert.AreEqual("string", props["message"]);
        Assert.AreEqual("integer", props["count"]);
    }

    [TestMethod]
    public void Register_IdentifiesRequiredParameters()
    {
        // Arrange
        var skillInstance = new TestSkills();

        // Act
        _registry.Register(skillInstance);

        // Assert
        var skill = _registry.FindSkill("skill_with_params");
        Assert.IsTrue(skill!.Parameters.Required.Contains("message"));
        // count is optional (has default value)
        Assert.IsFalse(skill.Parameters.Required.Contains("count"));
    }

    // ──── Search Tests ────

    [TestMethod]
    public void SearchSkills_ByKeyword()
    {
        // Arrange
        var skillInstance = new TestSkills();
        _registry.Register(skillInstance);

        // Act
        var skills = _registry.SearchSkills("skill");

        // Assert
        Assert.AreEqual(5, skills.Count);
        Assert.IsTrue(skills.Any(s => s.Name == "test_skill"));
        Assert.IsTrue(skills.Any(s => s.Name == "another_skill"));
    }

    [TestMethod]
    public void SearchSkills_ByDescription()
    {
        // Arrange
        var skillInstance = new TestSkills();
        _registry.Register(skillInstance);

        // Act
        var skills = _registry.SearchSkills("description");

        // Assert
        Assert.AreEqual(1, skills.Count);
        Assert.AreEqual("another_skill", skills[0].Name);
    }

    [TestMethod]
    public void SearchSkills_EmptyKeyword_ReturnsAll()
    {
        // Arrange
        var skillInstance = new TestSkills();
        _registry.Register(skillInstance);

        // Act
        var skills = _registry.SearchSkills("");

        // Assert
        Assert.AreEqual(5, skills.Count);
    }

    // ──── Role Filtering Tests ────

    [TestMethod]
    public void GetSkills_FiltersByRole()
    {
        // Arrange
        var skillInstance = new TestSkills();
        _registry.Register(skillInstance);

        // Act
        var skills = _registry.GetSkills(AgentRole.Worker);

        // Assert
        Assert.AreEqual(4, skills.Count); // All except restricted_skill
        Assert.IsFalse(skills.Any(s => s.Name == "restricted_skill"));
    }

    [TestMethod]
    public void GetSkills_Leader_CanAccessRestrictedSkills()
    {
        // Arrange
        var skillInstance = new TestSkills();
        _registry.Register(skillInstance);

        // Act
        var skills = _registry.GetSkills(AgentRole.Leader);

        // Assert
        Assert.AreEqual(5, skills.Count);
        Assert.IsTrue(skills.Any(s => s.Name == "restricted_skill"));
    }

    // ──── Execution Tests ────

    [TestMethod]
    public async Task ExecuteAsync_InvokesSkillMethod()
    {
        // Arrange
        var skillInstance = new TestSkills();
        _registry.Register(skillInstance);

        // Act
        var result = await _registry.ExecuteAsync(
            "test_skill", "{}", AgentRole.Spirit);

        // Assert
        Assert.IsTrue(result.Success);
        Assert.AreEqual("Executed", result.Output);
    }

    [TestMethod]
    public async Task ExecuteAsync_PassesArgumentsToSkill()
    {
        // Arrange
        var skillInstance = new TestSkills();
        _registry.Register(skillInstance);

        // Act
        var result = await _registry.ExecuteAsync(
            "skill_with_params",
            """{"message": "Hello", "count": 5}""",
            AgentRole.Spirit);

        // Assert
        Assert.IsTrue(result.Success);
        StringAssert.Contains(result.Output, "Hello");
        StringAssert.Contains(result.Output, "5");
    }

    [TestMethod]
    public async Task ExecuteAsync_EnforcesRoleRestrictions()
    {
        // Arrange
        var skillInstance = new TestSkills();
        _registry.Register(skillInstance);

        // Act
        var result = await _registry.ExecuteAsync(
            "restricted_skill", "{}", AgentRole.Worker);

        // Assert
        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Output, "Permission denied");
    }

    [TestMethod]
    public async Task ExecuteAsync_UnknownSkill_ReturnsError()
    {
        // Arrange
        var skillInstance = new TestSkills();
        _registry.Register(skillInstance);

        // Act
        var result = await _registry.ExecuteAsync(
            "non_existent_skill", "{}", AgentRole.Spirit);

        // Assert
        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Output, "Unknown skill");
    }

    [TestMethod]
    public async Task ExecuteAsync_AsyncMethod_ReturnsResult()
    {
        // Arrange
        var skillInstance = new TestSkills();
        _registry.Register(skillInstance);

        // Act
        var result = await _registry.ExecuteAsync(
            "async_skill", "{}", AgentRole.Spirit);

        // Assert
        Assert.IsTrue(result.Success);
        Assert.AreEqual("Async result", result.Output);
    }

    // ──── Edge Cases ────

    [TestMethod]
    public void FindSkill_CaseInsensitive()
    {
        // Arrange
        var skillInstance = new TestSkills();
        _registry.Register(skillInstance);

        // Assert
        Assert.IsNotNull(_registry.FindSkill("TEST_SKILL"));
        Assert.IsNotNull(_registry.FindSkill("test_skill"));
        Assert.IsNotNull(_registry.FindSkill("Test_Skill"));
    }

    [TestMethod]
    public void GetAllSkills_ReturnsReadOnlyList()
    {
        // Arrange
        var skillInstance = new TestSkills();
        _registry.Register(skillInstance);

        // Act
        var skills = _registry.GetAllSkills();

        // Assert
        Assert.ThrowsExactly<NotSupportedException>(() => 
            ((IList<SkillEntry>)skills).Add(null!));
    }
}

// Test skill class with various skill types
file sealed class TestSkills
{
    [PuddingSkill("A test skill", Group = "Test")]
    public string TestSkill() => "Executed";

    [PuddingSkill("Another skill with description", Group = "Test")]
    public string AnotherSkill() => "Another";

    [PuddingSkill("Restricted skill", Group = "Test", AllowedRoles = [AgentRole.Leader])]
    public string RestrictedSkill() => "Restricted";

    [PuddingSkill("Skill with parameters", Group = "Test")]
    public string SkillWithParams(
        [SkillParam("The message")] string message,
        [SkillParam("The count")] int count = 10)
        => $"{message}: {count}";

    [PuddingSkill("Async skill", Group = "Test")]
    public async Task<string> AsyncSkill()
    {
        await Task.Delay(1);
        return "Async result";
    }
}
