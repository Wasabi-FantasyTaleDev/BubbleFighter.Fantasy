namespace BubbleFighter.Fantasy.Contracts.Messenger;

/// <summary>
/// Game and region codes of the native messenger platform. Each member named after a key is registered by the
/// messenger service shipped with cn-774 (its NMService.exe sub_54C7B0 takes the code, the full and short display
/// names and the key), in that service's order; member names are those keys unchanged and the comments quote the
/// shipped names. A code reads as region byte, area byte and a 16-bit product: 0x220F is Bubble Fighter everywhere,
/// and area 0x02 is Taiwan (nexon_tw, kartrider_tw, cso_tw) while area 0x08 is the second Indonesian service
/// (kartrider_id2). Native clients compare friends, categories and catalog entries against one of these codes.
/// </summary>
#pragma warning disable CA1707, IDE1006 // Member names are the service's own keys.
public enum NexonGameCode : uint
{
    /// <summary>"UnknownGame" / "Unknown", key "unknown".</summary>
    unknown = 0x00000000,

    /// <summary>"서든어택" / "SA", key "SuddenAttack".</summary>
    SuddenAttack = 0x00017013,

    /// <summary>"APEX" / "에이펙스", key "apex".</summary>
    apex = 0x00012215,

    /// <summary>"제라", key "zera".</summary>
    zera = 0x00012202,

    /// <summary>"제라테스트", key "zeratest".</summary>
    zeratest = 0x0001F002,

    /// <summary>"제라개발테스트", key "zeratest_internal".</summary>
    zeratest_internal = 0x0001F004,

    /// <summary>"넥슨웹", key "web".</summary>
    web = 0x00011001,

    /// <summary>"워록", key "warrock".</summary>
    warrock = 0x00017004,

    /// <summary>"우당탕탕 대청소" / "대청소", key "trashbuster".</summary>
    trashbuster = 0x0001220E,

    /// <summary>"TestGame", key "testgame".</summary>
    testgame = 0x0001F001,

    /// <summary>"TalesWeaver", key "talesweaver".</summary>
    talesweaver = 0x00012208,

    /// <summary>"TalesWeaver", key "talesweaver_cn".</summary>
    talesweaver_cn = 0x01012208,

    /// <summary>"TalesWeaver", key "talesweaver_jp".</summary>
    talesweaver_jp = 0x01002208,

    /// <summary>"SugarRush", key "sugarrush".</summary>
    sugarrush = 0x02008102,

    /// <summary>"SP1", key "sp1".</summary>
    sp1 = 0x00017011,

    /// <summary>"슬랩샷 언더그라운드" / "슬랩샷", key "slapshot".</summary>
    slapshot = 0x0001700B,

    /// <summary>"슬랩샷 언더그라운드 테스트" / "슬랩샷테스트", key "slapshotinternal".</summary>
    slapshotinternal = 0x0001F00C,

    /// <summary>"샤이야" / "Shaiya", key "Shaiya".</summary>
    Shaiya = 0x00012225,

    /// <summary>"배틀스타 리로드" / "리로드", key "reload".</summary>
    reload = 0x00012221,

    /// <summary>"넥슨플러그", key "plug_kr".</summary>
    plug_kr = 0x00010002,

    /// <summary>"허스키 익스프레스" / "허스키", key "husky".</summary>
    husky = 0x0001200C,

    /// <summary>"허스키 익스프레스 Dev" / "허스키Dev", key "husky_dev".</summary>
    husky_dev = 0x0001F013,

    /// <summary>"NxOpp", key "nxopp".</summary>
    nxopp = 0x02008103,

    /// <summary>"구룡쟁패", key "ninedragons".</summary>
    ninedragons = 0x00017006,

    /// <summary>"Nexon", key "nexon_us".</summary>
    nexon_us = 0x02000000,

    /// <summary>"Nexon", key "nexon_tw".</summary>
    nexon_tw = 0x01020000,

    /// <summary>"넥슨", key "nexon_kr".</summary>
    nexon_kr = 0x00010000,

    /// <summary>"Nexon", key "nexon_jp".</summary>
    nexon_jp = 0x01000000,

    /// <summary>"Nexon", key "nexon_cn".</summary>
    nexon_cn = 0x01010000,

    /// <summary>"별", key "nexonbyul".</summary>
    nexonbyul = 0x00012206,

    /// <summary>"나나이모", key "nanaimo".</summary>
    nanaimo = 0x00017007,

    /// <summary>"나나이모테섭", key "nanaimotest".</summary>
    nanaimotest = 0x0001F006,

    /// <summary>"Motoloco", key "motoloco".</summary>
    motoloco = 0x02008101,

    /// <summary>"넥슨메신저", key "messenger".</summary>
    messenger = 0x00010001,

    /// <summary>"Lobby", key "messenger_cn".</summary>
    messenger_cn = 0x01010001,

    /// <summary>"루니아Z", key "lunia".</summary>
    lunia = 0x00017005,

    /// <summary>"루니아Z테섭", key "luniaZtest".</summary>
    luniaZtest = 0x0001F003,

    /// <summary>"ルニア戦記" / "ルニア", key "lunia_jp".</summary>
    lunia_jp = 0x01007005,

    /// <summary>"ルニア戦記Test" / "ルニアTest", key "luniatest_jp".</summary>
    luniatest_jp = 0x0100F003,

    /// <summary>"쿵파", key "koongpa".</summary>
    koongpa = 0x00017009,

    /// <summary>"쿵파온라인중국" / "쿵파중국", key "koongpa_cn".</summary>
    koongpa_cn = 0x01017009,

    /// <summary>"킥오프", key "kickoff".</summary>
    kickoff = 0x00017008,

    /// <summary>"킥오프RC", key "kickofftest".</summary>
    kickofftest = 0x0001F005,

    /// <summary>"카바티나스토리" / "카바티나", key "kavatina".</summary>
    kavatina = 0x00012210,

    /// <summary>"카트라이더" / "카트", key "kartrider".</summary>
    kartrider = 0x00012101,

    /// <summary>"카트라이더테스트" / "카트테스트", key "karttest".</summary>
    karttest = 0x00012102,

    /// <summary>"跑跑卡丁车" / "卡丁车", key "kartrider_cn".</summary>
    kartrider_cn = 0x01012101,

    /// <summary>"KartRider" / "Kart", key "kartrider_us".</summary>
    kartrider_us = 0x02002101,

    /// <summary>"KartRider" / "Kart", key "kartrider_th". The service ships the key "kartrider_th"; the code is appended because the key is not unique or not an identifier.</summary>
    kartrider_th_01032101 = 0x01032101,

    /// <summary>"KartRider" / "Kart", key "kartrider_th". The service ships the key "kartrider_th"; the code is appended because the key is not unique or not an identifier.</summary>
    kartrider_th_01042101 = 0x01042101,

    /// <summary>"KartRider" / "KR", key "kartrider_ru".</summary>
    kartrider_ru = 0x03012101,

    /// <summary>"KartRider" / "Kart", key "kartrider_id2".</summary>
    kartrider_id2 = 0x01082101,

    /// <summary>"跑跑卡丁車" / "卡丁車", key "kartrider_tw".</summary>
    kartrider_tw = 0x01022101,

    /// <summary>"カートライダー" / "カート", key "kartrider_jp".</summary>
    kartrider_jp = 0x01002101,

    /// <summary>"마비노기영웅전" / "영웅전", key "heroes".</summary>
    heroes = 0x0001200B,

    /// <summary>"마비노기영웅전 Test" / "영웅전 Test", key "heroes_test".</summary>
    heroes_test = 0x0001F00E,

    /// <summary>"마비노기영웅전 Dev" / "영웅전 Dev", key "heroes_dev".</summary>
    heroes_dev = 0x0001F00F,

    /// <summary>"vindictus", key "vindictus". The service ships the key "vindictus"; the code is appended because the key is not unique or not an identifier.</summary>
    vindictus_0200200B = 0x0200200B,

    /// <summary>"vindictus GM", key "vindictus GM". The service ships the key "vindictus GM"; the code is appended because the key is not unique or not an identifier.</summary>
    vindictus_GM_0200F01C = 0x0200F01C,

    /// <summary>"vindictus", key "vindictus". The service ships the key "vindictus"; the code is appended because the key is not unique or not an identifier.</summary>
    vindictus_0300200B = 0x0300200B,

    /// <summary>"vindictusGM", key "vindictusGM".</summary>
    vindictusGM = 0x0300F01C,

    /// <summary>"洛奇英雄传" / "英雄传", key "heroes_cn".</summary>
    heroes_cn = 0x0101200B,

    /// <summary>"洛奇英雄传Test" / "英雄传Test", key "heroestest_cn".</summary>
    heroestest_cn = 0x0101F00E,

    /// <summary>"洛奇英雄传GM" / "英雄传GM", key "heroesgm_cn".</summary>
    heroesgm_cn = 0x0101F01C,

    /// <summary>"マビノギ英雄伝", key "heroes_jp".</summary>
    heroes_jp = 0x0100200B,

    /// <summary>"マビノギ英雄伝GM", key "heroesgm_jp".</summary>
    heroesgm_jp = 0x0100F01C,

    /// <summary>"瑪寄英雄傳" / "英雄傳", key "heroes_tw".</summary>
    heroes_tw = 0x0102200B,

    /// <summary>"瑪寄英雄傳GM" / "英雄傳GM", key "heroesgm_tw".</summary>
    heroesgm_tw = 0x0102F01C,

    /// <summary>"제4구역", key "zone4".</summary>
    zone4 = 0x0001700A,

    /// <summary>"제4구역테섭", key "zone4internal".</summary>
    zone4internal = 0x0001F00D,

    /// <summary>"에버플래닛" / "에플", key "everplanet".</summary>
    everplanet = 0x0001700C,

    /// <summary>"에버플래닛Test" / "에플Test", key "everplanet_test".</summary>
    everplanet_test = 0x0001F011,

    /// <summary>"에버플래닛Dev" / "에플Dev", key "everplanet_dev".</summary>
    everplanet_dev = 0x0001F012,

    /// <summary>"エバープラネット", key "everplanet_jp".</summary>
    everplanet_jp = 0x0100700C,

    /// <summary>"エバープラネットTest", key "everplanet_test_jp".</summary>
    everplanet_test_jp = 0x0100F011,

    /// <summary>"エバープラネットDev", key "everplanet_dev_jp".</summary>
    everplanet_dev_jp = 0x0100F012,

    /// <summary>"엘소드", key "elsword".</summary>
    elsword = 0x00017010,

    /// <summary>"엘소드테스트", key "elswordtest".</summary>
    elswordtest = 0x0001F00A,

    /// <summary>"엘소드내부테스트", key "elswordtest_internal".</summary>
    elswordtest_internal = 0x0001F00B,

    /// <summary>"DungeonFighter", key "df_us".</summary>
    df_us = 0x02002211,

    /// <summary>"드래곤네스트" / "드래곤", key "dragonnest".</summary>
    dragonnest = 0x0001700D,

    /// <summary>"드래곤네스트 Test" / "드래곤Test", key "dragonnest_test".</summary>
    dragonnest_test = 0x0001F014,

    /// <summary>"드래곤네스트 Dev" / "드래곤Dev", key "dragonnest_dev".</summary>
    dragonnest_dev = 0x0001F015,

    /// <summary>"드래곤네스트 테스트서버" / "드래곤Open", key "dragonnest_opentest".</summary>
    dragonnest_opentest = 0x0001F01F,

    /// <summary>"데스크탑히어로즈" / "DH", key "desktopheroes".</summary>
    desktopheroes = 0x00012219,

    /// <summary>"카운터스트라이크Online2" / "CS온라인2", key "cso2".</summary>
    cso2 = 0x0001221C,

    /// <summary>"카운터-스트라이크 온라인" / "카스온라인", key "cso".</summary>
    cso = 0x00012009,

    /// <summary>"카운터-스트라이크 온라인 테섭" / "카스온라인테섭", key "csotest".</summary>
    csotest = 0x0001F007,

    /// <summary>"카운터-스트라이크 온라인 내부테섭" / "카스내부테섭", key "csointernal".</summary>
    csointernal = 0x0001F008,

    /// <summary>"카운터-스트라이크 온라인 개발테섭" / "카스개발테섭", key "csodevelopment".</summary>
    csodevelopment = 0x0001F009,

    /// <summary>"Counter-Strike Online" / "CSO", key "cso_tw".</summary>
    cso_tw = 0x01022009,

    /// <summary>"反恐精英OL", key "cso_cn".</summary>
    cso_cn = 0x01012009,

    /// <summary>"CounterStrikeOnline" / "CSO", key "cso_jp".</summary>
    cso_jp = 0x01002009,

    /// <summary>"CounterStrikeOnline" / "CSO", key "cso_sg".</summary>
    cso_sg = 0x01052009,

    /// <summary>"CounterStrikeOnline" / "CSO", key "cso_id".</summary>
    cso_id = 0x01062009,

    /// <summary>"CrazyArcade" / "CA", key "ca_jp".</summary>
    ca_jp = 0x0100200A,

    /// <summary>"컴뱃암즈", key "combatarms".</summary>
    combatarms = 0x00012207,

    /// <summary>"CombatArms", key "combatarms_us".</summary>
    combatarms_us = 0x02002207,

    /// <summary>"CombatArms", key "combatarms_eu".</summary>
    combatarms_eu = 0x03002207,

    /// <summary>"CombatArms", key "combatarms_br".</summary>
    combatarms_br = 0x04002207,

    /// <summary>"버블파이터" / "버파", key "bubblefighter"; the own-game code of every client build in its default locale.</summary>
    bubblefighter = 0x0001220F,

    /// <summary>"BF", key "bubblefighter_cn2"; chosen for locale 2 by kr-812 sub_8FE660, cn-324 sub_761F60 and cn-406 sub_794F50.</summary>
    bubblefighter_cn2 = 0x0107220F,

    /// <summary>"빅샷", key "bigshot".</summary>
    bigshot = 0x00012203,

    /// <summary>"炮炮火枪手" / "火枪手", key "bigshot_cn".</summary>
    bigshot_cn = 0x01012203,

    /// <summary>"에어라이더" / "에어", key "airrider".</summary>
    airrider = 0x0001200D,


    /// <summary>
    /// Bubble Fighter in area 0x02 (Taiwan), not registered by the cn-774 service; chosen for locale 3 by id-400
    /// sub_875BE0 and cn-406 sub_794F50.
    /// </summary>
    bubblefighter_0102220F = 0x0102220F,

    /// <summary>Bubble Fighter in area 0x06, not registered by the cn-774 service; chosen for locale 2 by cn-279 sub_6CFE60.</summary>
    bubblefighter_0106220F = 0x0106220F,

    /// <summary>
    /// Bubble Fighter in area 0x08 (the second Indonesian service), not registered by the cn-774 service; chosen for
    /// locale 4 by id-400 sub_875BE0 and id-1809 sub_89A550.
    /// </summary>
    bubblefighter_0108220F = 0x0108220F,
}
#pragma warning restore CA1707, IDE1006
