using CraftQueue.Server;
using SPTarkov.Server.Core.DI;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace CraftQueue.Tests;
public sealed class RecipeWarmupTests
{
    [Fact]
    public void TheWarmupIsAnUpdateComponentSptPicksUp()
    {
        Assert.True(typeof(IOnUpdate).IsAssignableFrom(typeof(RecipeCacheWarmup)));
        Assert.Contains(
            typeof(RecipeCacheWarmup).GetCustomAttributesData().Select(data => data.AttributeType.FullName),
            name => name == "SPTarkov.DI.Annotations.Injectable" || name == "SPTarkov.DI.Annotations.InjectableAttribute");
    }

    [Fact]
    public void OnLoadNoLongerStartsTheWarmup()
    {
        MethodInfo preload = typeof(WebServer).GetMethod("preloadRecipes", BindingFlags.Instance | BindingFlags.NonPublic)!;

        Assert.False(referencesMethod(onLoadBody(), preload));
        Assert.True(referencesMethod(
            typeof(WebServer).GetMethod(nameof(WebServer.StartRecipeWarmup))!.GetMethodBody()!.GetILAsByteArray()!,
            preload));
    }

    [Fact]
    public void TheApiSaysItIsStartingUntilEveryModHasLoaded()
    {
        MethodInfo moveNext = asyncBody(typeof(WebServer).GetMethod("handleSecurity", BindingFlags.Instance | BindingFlags.NonPublic)!);
        Assert.Contains("CraftQueue is still starting.", literals(moveNext));
        Assert.NotNull(typeof(WebServer).GetField("modsLoaded", BindingFlags.Instance | BindingFlags.NonPublic));
    }

    private static byte[] onLoadBody() =>
        asyncBody(typeof(WebServer).GetMethod(nameof(WebServer.OnLoadAsync))!).GetMethodBody()!.GetILAsByteArray()!;

    private static MethodInfo asyncBody(MethodInfo method)
    {
        Type stateMachine = method.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
        return stateMachine.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic)!;
    }
    private static List<string> literals(MethodInfo method)
    {
        byte[] il = method.GetMethodBody()!.GetILAsByteArray()!;
        var result = new List<string>();
        for (int i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != 0x72 || il[i + 4] != 0x70)
                continue;
            try
            {
                result.Add(method.Module.ResolveString(BitConverter.ToInt32(il, i + 1)));
            }
            catch (ArgumentException)
            {
            }
        }
        return result;
    }
    private static bool referencesMethod(byte[] il, MethodInfo method)
    {
        byte[] token = BitConverter.GetBytes(method.MetadataToken);
        for (int i = 0; i + 5 < il.Length; i++)
        {
            if (il[i] == 0xFE && il[i + 1] == 0x06
                && il[i + 2] == token[0] && il[i + 3] == token[1] && il[i + 4] == token[2] && il[i + 5] == token[3])
                return true;
        }
        return false;
    }
}
