using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Assembly = System.Reflection.Assembly;
using System.Xml.Linq;
using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnit;
using Xunit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Quant.Tests;

/// <summary>
/// **의존 방향을 코드로 고정한다.** 구조는 한 번 맞춰 놓아도 급한 수정 한 줄에 조용히 무너진다 —
/// 전략이 파일을 읽기 시작하거나, 화면이 주문 인터페이스를 받거나, 명령 하나가 연결부를 직접
/// 만들면 컴파일은 되지만 경계는 사라진다. 여기서 막는다.
///
///   ① 프로젝트 참조는 허용 목록 안에서만(전이 참조는 Directory.Build.props 가 끈다)
///   ② Core·Strategy 는 파일·네트워크를 쓰지 않는다 — 같은 입력이면 같은 판단
///   ③ Application 은 거래소 SDK 타입을 모른다 — 연결부를 바꿔도 실행 절차는 그대로
///   ④ 구현 선택은 Program·Composition 에서만
///   ⑤ 대시보드는 거래소를 읽기 인터페이스로만 받고, 조작은 명령함에 넣기만 한다
///
/// CI(.github/workflows/cs-architecture.yml)는 데이터가 필요 없는 이 테스트만 돌린다.
/// </summary>
[Trait("Category", "Architecture")]
public class ArchitectureTests
{
    // ── ① 프로젝트 참조 허용 목록 ────────────────────────────────

    /// <summary>
    /// 프로젝트별로 **직접 참조해도 되는** 프로젝트. 여기 없는 참조가 csproj 에 생기면 실패한다.
    /// 새 프로젝트를 만들면 여기에 먼저 자리를 정해야 한다(목록에 없는 프로젝트도 실패).
    /// </summary>
    static readonly Dictionary<string, string[]> Allowed = new()
    {
        ["Core"] = Array.Empty<string>(),
        ["Strategy"] = new[] { "Core" },
        ["Application"] = new[] { "Core", "Strategy" },
        ["Feeds"] = new[] { "Core", "Application" },
        ["Exchange"] = new[] { "Core", "Application" },
        ["Exchange.BinanceNet"] = new[] { "Core", "Application" },
        ["Infra"] = new[] { "Core", "Application" },
        // 실행 호스트 — Application 과 그것이 배선하는 구현들.
        ["Live"] = new[] { "Core", "Strategy", "Application", "Feeds",
                                 "Exchange", "Exchange.BinanceNet", "Infra" },
        // 감시 화면 — Application 읽기 기능과 읽기 어댑터. Strategy 는 StrategyEye 의 항목별 점수 분해가 쓴다
        // (개편안과 다른 예외 — 2026-09-14 사용자 결정으로 유지).
        ["Dashboard"] = new[] { "Core", "Strategy", "Application", "Feeds",
                                      "Exchange", "Infra" },
    };

    /// <summary>테스트는 무엇이든 참조할 수 있다. 대신 아무도 테스트를 참조하지 않는다.</summary>
    const string TestProject = "Tests";

    static string CsRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Quant.sln"))) return d.FullName;
        throw new InvalidOperationException("Quant.sln 을 찾지 못했다 — src 폴더 밖에서 돌고 있다");
    }

    static Dictionary<string, List<string>> ProjectReferences()
    {
        var result = new Dictionary<string, List<string>>();
        foreach (string csproj in Directory.GetFiles(CsRoot(), "*.csproj", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(CsRoot(), csproj).Replace('\\', '/');
            if (rel.Contains("/bin/") || rel.Contains("/obj/") || rel.StartsWith("experiments/")) continue;
            if (rel.Count(c => c == '/') != 1) continue;        // src/<프로젝트>/<프로젝트>.csproj 만 — 안쪽의 독립 실험은 제외
            string name = Path.GetFileNameWithoutExtension(csproj);
            result[name] = XDocument.Load(csproj).Descendants("ProjectReference")
                .Select(e => Path.GetFileNameWithoutExtension(e.Attribute("Include")!.Value.Replace('\\', '/')))
                .ToList();
        }
        return result;
    }

    [Fact]
    public void 프로젝트_참조는_허용_목록_안에서만()
    {
        var violations = new List<string>();
        foreach (var (project, refs) in ProjectReferences())
        {
            if (project == TestProject) continue;
            if (!Allowed.TryGetValue(project, out string[]? allowed))
            {
                violations.Add($"{project}: 허용 목록에 없는 프로젝트 — ArchitectureTests.Allowed 에 자리를 정할 것");
                continue;
            }
            foreach (string r in refs.Where(r => !allowed.Contains(r)))
                violations.Add($"{project} → {r}: 허용되지 않은 참조");
        }
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    /// <summary>
    /// 실험(src/experiments/)은 독립 실행이다 — 운영 코드는 실험을 참조하지 않는다. 테스트만 실험의
    /// 계산을 검증하려고 참조할 수 있다. 아무도 테스트를 참조하지 않는다.
    /// </summary>
    [Fact]
    public void 운영_코드는_테스트나_실험을_참조하지_않는다()
    {
        string root = CsRoot();
        var experiments = Directory.Exists(Path.Combine(root, "experiments"))
            ? Directory.GetFiles(Path.Combine(root, "experiments"), "*.csproj", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                            && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .Select(Path.GetFileNameWithoutExtension).ToHashSet()
            : new HashSet<string?>();
        var violations = ProjectReferences()
            .SelectMany(kv => kv.Value
                .Where(r => r == TestProject || (kv.Key != TestProject && experiments.Contains(r)))
                .Select(r => $"{kv.Key} → {r}"))
            .ToList();
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    [Fact]
    public void 전이_참조가_꺼져_있다()
    {
        string props = Path.Combine(CsRoot(), "Directory.Build.props");
        Assert.True(File.Exists(props), "Directory.Build.props 가 없다");
        string? value = XDocument.Load(props).Descendants("DisableTransitiveProjectReferences").FirstOrDefault()?.Value;
        Assert.Equal("true", value?.Trim());
    }

    // ── ②~⑤ 타입 의존 ─────────────────────────────────────────────

    static readonly Assembly Core = typeof(Quant.Core.Bar).Assembly;
    static readonly Assembly Strategy = typeof(Quant.Strategy.CalcInd).Assembly;
    static readonly Assembly App = typeof(Quant.Application.Ports.IFuturesExchange).Assembly;
    static readonly Assembly Feeds = typeof(Quant.Feeds.ReplayMarketData).Assembly;
    static readonly Assembly Exchange = typeof(Quant.Exchange.PaperFutures).Assembly;
    static readonly Assembly BinanceNet = Assembly.Load("Exchange.BinanceNet");
    static readonly Assembly Infra = typeof(Quant.Infra.FileLog).Assembly;
    static readonly Assembly Live = Assembly.Load("Live");
    static readonly Assembly Dashboard = typeof(Quant.Dashboard.SnapshotService).Assembly;

    static readonly Architecture Arch = new ArchLoader()
        .LoadAssemblies(Core, Strategy, App, Feeds, Exchange, BinanceNet, Infra, Live, Dashboard)
        .Build();

    [Fact]
    public void Core_와_Strategy_는_파일_네트워크를_쓰지_않는다()
    {
        IArchRule rule = Types().That().ResideInAssembly(Core, Strategy)
            .Should().NotDependOnAny(Types(true).That().ResideInNamespaceMatching(@"^System\.(IO|Net)(\..+)?$"))
            .Because("판단 코드는 같은 입력이면 같은 답을 내야 한다 — 파일·네트워크는 실행 쪽 포트 뒤에 둔다");
        rule.Check(Arch);
    }

    [Fact]
    public void Application_은_거래소_SDK_타입을_모른다()
    {
        IArchRule rule = Types().That().ResideInAssembly(App)
            .Should().NotDependOnAny(Types(true).That().ResideInNamespaceMatching(@"^(Binance\.Net|CryptoExchange\.Net)(\..+)?$"))
            .Because("연결부를 바꿔도 실행·복구 절차가 그대로여야 한다");
        rule.Check(Arch);
    }

    /// <summary>Program(최상위 문장과 그 람다)·Composition 만 구현을 고른다.</summary>
    const string CompositionRoots = @"^(Program|Quant\.(Live|Dashboard)\.Composition)([+/].*)?$";

    [Fact]
    public void 실행_호스트는_Composition_밖에서_구현을_만들지_않는다()
    {
        IArchRule constructors = Types().That().ResideInAssembly(Live).And().DoNotHaveFullNameMatching(CompositionRoots)
            .Should().NotCallAny(MethodMembers().That().AreConstructors()
                .And().AreDeclaredIn(Types().That().ResideInAssembly(Exchange, BinanceNet, Infra, Feeds)))
            .Because("거래소·저장·알림 구현은 Composition 에서만 고른다");
        constructors.Check(Arch);

        IArchRule factories = Types().That().ResideInAssembly(Live).And().DoNotHaveFullNameMatching(CompositionRoots)
            .Should().NotCallAny(MethodMembers().That()
                .HaveFullNameMatching(@"Quant\.Infra\.(FileLog::Install|StateFiles::Open|ProcessLock::Acquire|DotEnv::Load)\("))
            .Because("로그·장부 저장·프로세스 잠금·설정 파일도 구현 선택이다");
        factories.Check(Arch);
    }

    [Fact]
    public void 대시보드는_Composition_밖에서_출처를_만들지_않는다()
    {
        IArchRule rule = Types().That().ResideInAssembly(Dashboard).And().DoNotHaveFullNameMatching(CompositionRoots)
            .Should().NotCallAny(MethodMembers().That().AreConstructors()
                .And().AreDeclaredIn(Types().That().ResideInAssembly(Exchange, BinanceNet, Infra, Feeds)
                    .Or().Are(typeof(Quant.Dashboard.Sources.ExchangeSource), typeof(Quant.Dashboard.Sources.MarketSource),
                              typeof(Quant.Dashboard.Sources.EquityLog), typeof(Quant.Dashboard.Sources.SpotBalanceReader),
                              typeof(Quant.Dashboard.Sources.SshBotSource), typeof(Quant.Dashboard.Sources.LocalBotSource),
                              typeof(Quant.Dashboard.Sources.PerformanceSource), typeof(Quant.Dashboard.Sources.LabSource),
                              typeof(Quant.Dashboard.Sources.LiveJournalSource), typeof(Quant.Dashboard.Sources.LivePositionsSource),
                              typeof(Quant.Dashboard.Sources.PublicSpotPrices))))
            .Because("화면이 무엇으로 읽는지는 Composition 에서만 고른다");
        rule.Check(Arch);
    }

    [Fact]
    public void 대시보드는_거래소를_읽기_인터페이스로만_받는다()
    {
        IArchRule rule = Types().That().ResideInAssembly(Dashboard).And().DoNotHaveFullNameMatching(CompositionRoots)
            .Should().NotDependOnAny(Types().That().ResideInAssembly(Exchange, BinanceNet)
                .Or().Are(typeof(Quant.Application.Ports.IFuturesExchange), typeof(Quant.Application.Ports.IFuturesOrders),
                          typeof(Quant.Application.Ports.ISpotExchange), typeof(Quant.Application.Orders.Executor),
                          typeof(Quant.Application.Ports.IOperatorQueue), typeof(Quant.Application.Live.OperatorDesk)))
            .Because("화면은 주문을 내지 않는다");
        rule.Check(Arch);
    }

    /// <summary>
    /// 대시보드는 **보기 전용**이다(2026-09-16). 화면에서 멈추고 청산하던 명령함 경로를 인터넷 공개에 앞서
    /// 없앴다 — 로그인·토큰이 새도 계좌를 건드릴 길이 없어야 한다. Composition·Program 도 예외가 아니다
    /// (예전엔 Composition 이 명령함을 만들었다). 조작 경로를 되살리려면 이 테스트부터 지워야 한다.
    /// </summary>
    [Fact]
    public void 대시보드는_봇_명령함에_손대지_않는다()
    {
        IArchRule rule = Types().That().ResideInAssembly(Dashboard)
            .Should().NotDependOnAny(Types().That().Are(
                typeof(Quant.Application.Ports.IOperatorInbox), typeof(Quant.Application.Ports.IOperatorQueue),
                typeof(Quant.Application.Operator.OperatorCommand), typeof(Quant.Infra.FileOperatorInbox),
                typeof(Quant.Application.Live.OperatorDesk)))
            .Because("대시보드는 보기 전용이다 — 인터넷에 공개되는 화면에 계좌를 움직이는 경로를 두지 않는다");
        rule.Check(Arch);
    }
}
