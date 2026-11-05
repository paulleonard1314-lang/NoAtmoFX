// NoAtmoFX: weather-driven volumetric clouds and atmosphere for Nuclear Option (URP).
// URP passes are injected through RenderPipelineManager.beginCameraRendering + EnqueuePass, main camera only.
// No FindObjectsOfType per frame: everything comes from the game's singletons.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.Universal;

namespace NoAtmoFX
{
    [BepInPlugin("paul.no.clouds", "NoAtmoFX", Version)]   // GUID kept from NO Clouds so existing settings carry over
    public class NOCloudsPlugin : BaseUnityPlugin
    {
        public const string Version = "1.3.0";
        internal static ManualLogSource Log;

        // General
        internal ConfigEntry<bool> _on; ConfigEntry<bool> _hideGame, _debug, _jitter, _useDepth, _gameWeather;
        ConfigEntry<int> _event;
        ConfigEntry<float> _ciTile;
        ConfigEntry<bool> _ownCirrus;
        ConfigEntry<int> _farSteps; ConfigEntry<float> _farShadow;
        ConfigEntry<bool> _shOn; ConfigEntry<float> _shDist, _shOpacity, _shBelow, _shProt; ConfigEntry<int> _shRes, _shSteps;
        ConfigEntry<KeyboardShortcut> _toggleKey, _menuKey, _dumpKey;
        ConfigEntry<bool> _wgOn; ConfigEntry<float> _wgInt, _wgFreeze, _wgRainTau, _wgFrostReach, _wgFrostWhite, _wgRefract; ConfigEntry<int> _wgTest, _wgRainSrc; ConfigEntry<float> _wgSize, _wgAir;
        internal NOCWetGlass Wet; WetGlassPass _gpass; AssetBundle _ab;
        internal NOCNukes Nukes; internal NOCAtmos Atmos; AtmosPass _apass; internal bool RaysActive, ShaftsActive; internal int DownscaleNow = 2; internal RenderTexture CbRT, ReflCloudRT; internal Vector2 GDLast;
        internal float ProbeCloudAt, ProbeTauAt; internal bool ProbeAtValid;
        ConfigEntry<int> _atNearRain; ConfigEntry<bool> _atShafts; ConfigEntry<float> _atRainVis, _atRainbow, _atGlory, _atLightning, _atTurb, _atRays, _atMoon, _wetDark, _stormDim, _atFog, _atShafts2; ConfigEntry<bool> _keepSun; ConfigEntry<float> _deck; ConfigEntry<int> _lang; ConfigEntry<float> _supercell; ConfigEntry<bool> _waterLight; ConfigEntry<int> _waterRefl; ConfigEntry<bool> _probeClouds; ConfigEntry<float> _skyCirrusCap; ConfigEntry<float> _maxCloudSpeed;
        internal RenderTexture ProbeRT;
        // Layer
        ConfigEntry<float> _baseOffset, _manualBase, _planetR;
        // Shape
        ConfigEntry<float> _coverage, _density, _erosion, _topDensity, _dome, _billow, _soft, _shear, _baseRough;
        // Scale
        ConfigEntry<float> _perC, _perCell, _perS, _perD, _windMul, _windSpeed;
        ConfigEntry<float> _blur, _farSoft, _shDark; ConfigEntry<float> _warp, _perWarp, _turb, _perTurb, _sizeMix, _stretch, _veer, _evolve;
        ConfigEntry<float> _cbTop, _perCb, _anvil, _anvilShift;
        // Weather model
        ConfigEntry<float> _condOv, _changeMin, _todOv, _humBias, _insBias, _baseVar, _cirrusAmt, _midAmt, _frontDist, _frontWidth, _perHum, _perIns;
        ConfigEntry<bool> _diurnal;
        ConfigEntry<int> _frontMode;
        // Lighting
        ConfigEntry<float> _absorb, _lightAbsorb, _g1, _g2, _gMix, _powder, _powderK, _sunMul, _ambMul, _exposure, _msA, _msB, _sunWhite, _fogAmt;
        ConfigEntry<int> _tonemap;
        // Quality
        ConfigEntry<int> _steps, _lightSteps, _downscale;
        ConfigEntry<float> _minStep, _stepGrowth, _maxStep, _maxDist;
        ConfigEntry<bool> _mapSlicing, _perfMeter; ConfigEntry<int> _shEvery, _temporal; internal ConfigEntry<string> _preset;

        Material _mat;
        CloudPass _pass;
        WaterPass _wpass;
        ShadowPass _spass;
        internal RenderTexture WxA, WxB, WxC, WxP, ShadowRT, ShadowDRT;
        internal int ShDFrame = -1; internal Camera ShDCam;
        internal int WxFrame = -1;
        internal RenderTexture CloudRT;
        internal RenderTexture CloudImg;   // the cloud image composited this frame (CloudRT, or the temporal history)
        internal int RTFrame = -1;
        internal Material Mat => _mat;
        internal float WExtNow => _maxDist.Value;
        bool _menu;
        Rect _win = new Rect(40, 40, 620, 680);
        Vector2 _scroll;
        string _section = "Shape";
        CursorLockMode _prevLock; bool _prevVis;

        // game state (cached)
        LevelInfo _level;
        Component _cloudLayer;
        object _weatherSet;
        readonly List<Renderer> _hidden = new List<Renderer>();
        internal double _windX, _windZ, _evolveT; Vector2 _windDirL; bool _windDirSet; internal Vector3 _windS; float _windLogT = -100;
        internal float _Hs, _Is, _heatS, _condS, _baseS, _frontAmt; internal bool _wxInit; bool _frontInit;
        internal float _effCoverage = 0.5f, _effBottom = 1500, _effTop = 4500, _H, _I, _heat, _cond, _tod, _ci; internal int _front;
        internal NOCSync Sync; internal NOCBoscali Boscali; internal NOCMountainFog Oro; internal float EvolveRate => _evolve.Value;
        readonly Dictionary<Camera, UniversalAdditionalCameraData> _camData = new Dictionary<Camera, UniversalAdditionalCameraData>();
        readonly Vector3[] _corners = new Vector3[4];

        static readonly FieldInfo F_cloudLayer = typeof(LevelInfo).GetField("cloudLayer", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        static FieldInfo F_ws;

        // ---------- language: config is in English, the menu can also be shown in Portuguese ----------
        static readonly Dictionary<string, string> PtDesc = new Dictionary<string, string>
        {
            { "Multiplayer/SyncClouds", "No multijogador as nuvens seguem o relógio da missão que o jogo compartilha, então todos os jogadores veem as mesmas nuvens (também em servidores sem mods). Desligado = comportamento antigo (cada um com a sua deriva)." },
            { "Multiplayer/FollowHostSettings", "Quando o host também usa o NoAtmoFX: usar as configurações de nuvem do host (Weather, Layer, Shape, Scale, Variation, Wind, Storm, Nuclear) enquanto estiver no jogo dele, pra nuvem ficar idêntica. As suas voltam quando você sai. Luz, qualidade, vidro e atmosfera continuam as suas." },
            { "Nuclear/Enabled", "Explosões nucleares empurram as nuvens: abre um buraco atrás da onda de choque, a nuvem se acumula na borda e anéis de condensação se formam em volta do cogumelo." },
            { "Nuclear/HoleSize", "Tamanho do buraco que a explosão abre nas nuvens, em raios de explosão ((kg de TNT)^(1/3) m: 1 Mt = 1 km). 7 = 7 km para 1 Mt." },
            { "Nuclear/WilsonCloud", "Casca de condensação atrás da onda de choque nos primeiros segundos (mais forte em ar úmido). 0 = desliga." },
            { "Nuclear/Rings", "Capuzes (pileus) e saias (velum) em volta do cogumelo subindo. 0 = desliga." },
            { "Nuclear/RingSize", "Até onde as saias (velum) se espalham, em raios de explosão (8 = 8 km para 1 Mt)." },
            { "Nuclear/FireballLight", "Luz da bola de fogo nas nuvens. 0 = desliga." },
            { "Nuclear/Duration", "Minutos até o buraco fechar e os anéis sumirem (devagar, na segunda metade)." },
            { "Nuclear/MinYield", "Menor explosão (quilotons) que mexe nas nuvens quando o efeito não tem cogumelo." },
            { "Nuclear/UnlockNukes", "Teste: o jogador pode escolher armas nucleares em qualquer missão (Free Flight também), ignorando escalada, patente e estoque de ogivas. Só funciona sozinho ou como host. A IA não muda." },
            { "Nuclear/TestBlastKey", "Teste: uma explosão (só nas nuvens, sem dano) à frente da câmera." },
            { "Nuclear/TestYield", "Potência da explosão de teste em quilotons." },
            { "Nuclear/TestDistance", "Distância da explosão de teste à frente da câmera (km), 500 m acima do mar." },
            { "General/ConfigVersion", "Não mexer: versão dos padrões (a config é refeita quando muda)." },
            { "General/Enabled", "Liga as nuvens volumétricas." },
            { "General/ToggleKey", "Liga/desliga as nuvens." },
            { "General/MenuKey", "Abre o menu de ajuste ao vivo." },
            { "General/DumpKey", "Escreve no log o diagnóstico (câmera, clima, URP)." },
            { "General/HideGameClouds", "Esconde as nuvens do jogo para não duplicar: as de partícula e as nuvens cinzas de tempestade pintadas no céu (o relâmpago e as sombras no chão continuam)." },
            { "General/FollowGameWeather", "Seguir o clima da missão: altura da camada (editor de missão), vento e cobertura do jogo." },
            { "General/DebugView", "Diagnóstico: pinta a opacidade da nuvem de vermelho." },
            { "General/AnimatedJitter", "Varia o ruído dos passos a cada quadro (menos faixas, um pouco de chiado)." },
            { "General/UseSceneDepth", "Corta as nuvens atrás de terreno/aviões usando a profundidade da cena." },
            { "General/RenderOrder", "Quando desenhar: 0 = antes dos transparentes (chama do motor, fumaça e explosões por cima das nuvens; a água do mar é desenhada pelo mod antes das nuvens), 1 = depois dos transparentes (antigo)." },
            { "Layer/BaseOffset", "Ajuste na base de referência (a altura de nuvens da missão). A base real de cada região vem da umidade." },
            { "Layer/ManualBase", "Base de referência (m) quando FollowGameWeather está desligado." },
            { "Layer/PlanetRadius", "Raio usado para a curvatura do horizonte (o mapa é plano; isto só curva a camada ao longe)." },
            { "Shape/CoverageScale", "Multiplica a cobertura de cúmulos que o modelo de tempo calcula." },
            { "Shape/Density", "Extinção por metro dentro da nuvem." },
            { "Shape/Dome", "Quanto a nuvem afina com a altura (maior = domos mais arredondados e baixos; menor = torres)." },
            { "Shape/Billow", "Bolhas de couve-flor na superfície da nuvem." },
            { "Shape/Softness", "Borda da nuvem (menor = contorno mais definido)." },
            { "Shape/DetailErosion", "Quanto o ruído fino come das bordas (fiapos na base, couve-flor no topo)." },
            { "Shape/TopDensity", "Densidade no topo relativa à base." },
            { "Shape/WindShear", "Cisalhamento: o topo é empurrado a favor do vento (×8 em 8 km de altura)." },
            { "Scale/ClusterPeriod", "Tamanho dos aglomerados de nuvens (m)." },
            { "Scale/CellPeriod", "Tamanho de cada nuvem (célula) (m). Menor = nuvens menores e mais numerosas." },
            { "Scale/BillowPeriod", "Tamanho das bolhas (m)." },
            { "Scale/DetailPeriod", "Tamanho do detalhe fino (m)." },
            { "Scale/GameWindMultiplier", "Multiplicador do vento do jogo arrastando as nuvens." },
            { "Scale/WindSpeed", "Vento (m/s) quando FollowGameWeather está desligado." },
            { "Shape/Blur", "Blur leve na imagem das nuvens (pixels). 0 = desligado." },
            { "Shape/FarSoftness", "Bordas mais suaves com a distância (0 = igual perto e longe)." },
            { "Variation/DomainWarp", "Distorção do mapa de nuvens (quebra qualquer grade/padrão)." },
            { "Variation/WarpPeriod", "Tamanho da distorção (m)." },
            { "Variation/Turbulence", "Turbulência 3D que entorta as nuvens com a altura (m)." },
            { "Variation/TurbulencePeriod", "Tamanho da turbulência (m)." },
            { "Variation/SizeMix", "Quantidade de nuvens pequenas/médias soltas (0 = só as grandes, 0,3 = poucas pequenas, 0,7 = padrão, 1 = muitas)." },
            { "Variation/CloudStreets", "Esticar as nuvens na direção do vento (1 = redondas; 2+ = ruas de nuvens)." },
            { "Wind/WindVeer", "Quanto a direção do cisalhamento gira até 8 km de altura (rad)." },
            { "Wind/Evolution", "Velocidade com que as bolhas 'fervem'/mudam de forma (m/s). 0 = nuvens só deslizam." },
            { "Weather/ConditionsOverride", "Tempo: -1 = o da missão; 0 = céu de bom tempo ... 1 = tempestade." },
            { "Shape/BaseRoughness", "Parte de baixo dos cúmulos: 0 = todas cortadas retas na base da camada; mais alto = cada nuvem um pouco acima (até ~180 m), fundo subindo para as bordas e mais macio." },
            { "Weather/ChangeTime", "Minutos que as nuvens levam para acompanhar uma mudança de tempo (condições, base, frente, ciclo do dia). Nuvem de verdade nunca muda de uma vez." },
            { "Weather/TimeOfDayOverride", "Hora usada pelo ciclo do dia: -1 = a da missão." },
            { "Weather/DiurnalCycle", "Ciclo do dia: o sol aquece o chão, os cúmulos crescem à tarde e somem à noite." },
            { "Weather/HumidityBias", "Mais/menos umidade (base mais baixa, mais nuvens, estratos)." },
            { "Weather/InstabilityBias", "Mais/menos instabilidade (nuvens mais altas, tempestades)." },
            { "Layer/OvercastDeck", "Com tempo encoberto até tempestade, a camada média vira um lençol que cobre o céu todo (0 = desliga)." },
            { "Weather/BaseVariation", "Quanto a base muda entre ar úmido e seco (m)." },
            { "Weather/HumidityPeriod", "Tamanho das regiões de umidade (m)." },
            { "Weather/InstabilityPeriod", "Tamanho das regiões de instabilidade (m)." },
            { "Weather/GameCirrus", "Quanto do tempo o céu do jogo (as cirrus dele) pode mostrar: as 'condições' do jogo ficam limitadas a esse valor no céu. 0 = sem cirrus do jogo; acima de ~0,6 volta o lençol cinza com padrão do jogo." },
            { "Weather/OwnCirrus", "Desenhar cirros próprios do mod (desligado: ficam os cirros do próprio jogo)." },
            { "Weather/CirrusScale", "Tamanho da textura de cirros no céu (m). Maior = fios mais longos." },
            { "Weather/Cirrus", "Quantidade de cirros (fiapos altos)." },
            { "Weather/MidLevel", "Quantidade de altocúmulos/altoestratos (camada média)." },
            { "Weather/Front", "Frente: 0 nenhuma, 1 fria, 2 quente, 3 automática (pelo tempo da missão)." },
            { "Weather/FrontDistanceKm", "Onde a frente começa, contra o vento (km). Ela chega com o vento." },
            { "Weather/FrontWidthKm", "Largura da faixa da frente (km)." },
            { "General/CloudsInReflections", "Desenha estas nuvens no probe de reflexo do mapa, para o mar (e superfícies brilhantes) refletir elas." },
            { "General/WaterReflection", "Reflexo do mar quando o mod desenha o mar: 0 = auto (igual ao jogo), 1 = probe de reflexo do mapa, 2 = céu simples." },
            { "General/WaterLighting", "Dá ao mar a luz ambiente e o reflexo do jogo quando o mod desenha ele antes das nuvens (RenderOrder 0). Desligue se o mar ficar estranho." },
            { "Wind/MaxCloudSpeed", "Velocidade máxima com que as nuvens andam com o vento do jogo (m/s). As rajadas do jogo em tempestade podem ser fortíssimas; as nuvens seguem uma média suave limitada a isto." },
            { "Storm/Supercells", "Fração das tempestades fortes que viram supercélula: uma célula enorme girando, com bigorna gigante, linha de flanco e nuvem-parede (0 = desliga)." },
            { "Storm/CbTop", "Topo dos cumulonimbus / bigornas (m acima do mar)." },
            { "Storm/CbCellPeriod", "Espaçamento entre cumulonimbus (m)." },
            { "Storm/Anvil", "Largura da bigorna." },
            { "Storm/AnvilDrift", "Quanto a bigorna se estende a favor do vento (m)." },
            { "Lighting/StormLight", "Em tempestade, quanto o sol direto some e a luz fica cinza e difusa (0 = como em tempo bom)." },
            { "Lighting/WetDarkening", "Nuvens carregadas d'água (cúmulo-nimbo, cúmulo grosso, céu fechado/chuvoso) ficam mais escuras por baixo e dos lados. 0 = desliga." },
            { "Lighting/ShadowDarkness", "Quão escuras ficam as partes da nuvem na sombra (lado oposto ao sol, base, nuvem sob nuvem). 0 = claras, 1 = bem escuras." },
            { "Lighting/Absorption", "Multiplicador de extinção na visão." },
            { "Lighting/LightAbsorption", "Extinção no caminho até o sol." },
            { "Lighting/PhaseForward", "Espalhamento para frente." },
            { "Lighting/PhaseBack", "Espalhamento para trás." },
            { "Lighting/PhaseMix", "Mistura das duas fases." },
            { "Lighting/Powder", "Efeito powder." },
            { "Lighting/PowderSharpness", "Nitidez do powder." },
            { "Lighting/SunIntensity", "Multiplicador da luz do sol do jogo." },
            { "Lighting/AmbientIntensity", "Multiplicador da luz ambiente do jogo (céu)." },
            { "Lighting/Exposure", "Exposição." },
            { "Lighting/Tonemap", "0 = nunca, 1 = sempre, 2 = automático (só se a câmera não for HDR)." },
            { "Lighting/MultiScatterEnergy", "Energia de cada oitava de espalhamento múltiplo." },
            { "Lighting/MultiScatterAttenuation", "Atenuação de cada oitava." },
            { "Lighting/SunWhiten", "Quanto clarear a cor do sol em direção ao branco." },
            { "Lighting/GameFog", "Quanto a névoa do jogo se aplica às nuvens." },
            { "Quality/Steps", "Máximo de passos de raymarch." },
            { "Quality/LightSteps", "Passos em direção ao sol." },
            { "Quality/Downscale", "1 = resolução cheia, 2 = metade, 4 = um quarto." },
            { "Quality/MinStep", "Passo mínimo (m)." },
            { "Quality/Preset", "Preset de qualidade: Low, Medium, High ou Ultra (Ultra = visual completo, todo pixel calculado todo quadro). Escolher um ajusta Downscale, Temporal, Steps, LightSteps, MaxStep, MaxDistance e a resolução/passos da sombra; mexer em qualquer um desses à mão vira Custom." },
            { "Quality/Temporal", "Reprojeção temporal: 0 = desligada (todo pixel de nuvem calculado todo quadro), 1 = metade dos pixels por quadro (xadrez), 2 = um quarto por quadro (2x2). O resto vem dos quadros anteriores, movido com a câmera e preso às cores dos pixels novos em volta. Raios, clarões nucleares e cortes de câmera sempre são desenhados inteiros." },
            { "Quality/StepGrowth", "Quanto o passo cresce com a distância." },
            { "Quality/MaxStep", "Passo máximo (m)." },
            { "Quality/MaxDistance", "Distância máxima desenhada (m)." },
            { "Quality/MapSlicing", "Mapas de tempo ficam parados e são atualizados um por quadro (só recentram quando a câmera anda ~1 km); o reflexo do mar reaproveita os mesmos. Mesmo visual, menos GPU. Desligado = refaz todos a cada quadro, como na 1.1." },
            { "Quality/ShadowUpdateEvery", "Os mapas de sombra das nuvens são redesenhados a cada N quadros (o cookie e o mapa de profundidade revezam). 1 = todo quadro, como na 1.1." },
            { "Quality/PerformanceMeter", "Mostra quantos milissegundos cada parte do mod gasta (nesta aba). Custa quase nada, mas deixe desligado quando não estiver olhando." },
            { "Shadows/CloudOnCloudSteps", "Passos longos em direção ao sol (sombra de uma nuvem sobre outra, até ~7 km). 0 = desliga." },
            { "Shadows/CloudOnCloudStrength", "Força da sombra de nuvem sobre nuvem." },
            { "Shadows/Shadows", "Sombras das nuvens no chão e nos aviões (substitui o cookie do sol do jogo)." },
            { "Shadows/ShadowDistance", "Tamanho da área com sombra em volta da câmera (m)." },
            { "Shadows/ShadowResolution", "Resolução do mapa de sombra." },
            { "Shadows/ShadowOpacity", "Quanto a sombra escurece a luz do sol." },
            { "Shadows/ShadowSteps", "Passos através da camada de nuvens." },
            { "Shadows/OwnAircraftRadius", "Raio (m) em volta da câmera onde nuvens abaixo dela não sombreiam (protege o seu avião). O resto do chão recebe sombra de todas as nuvens." },
            { "Shadows/ShadowFromBelow", "1 = nuvens abaixo da câmera sombreiam até o seu avião (normalmente 0)." },
            { "Glass/WetGlass", "Gotas na canopy dentro da nuvem/chuva e geada acima da altitude de congelamento." },
            { "Glass/Intensity", "Quantidade de água/gelo no vidro." },
            { "Glass/FreezingAltitude", "Altitude (m) acima da qual a nuvem congela no vidro (geada em vez de gotas)." },
            { "Glass/RainThickness", "Quão grossa a nuvem acima precisa ser para chover em você (menor = chove mais fácil; 0 = sem chuva)." },
            { "Glass/FrostReach", "Até onde a geada avança das bordas para o meio do vidro." },
            { "Glass/FrostWhite", "Quão branca/opaca é a geada." },
            { "Glass/Refraction", "Quanto as gotas distorcem a imagem." },
            { "Glass/DropSize", "Tamanho das gotas." },
            { "Glass/Airflow", "Força do vento no vidro: maior = gotas viram riscos mais cedo/mais devagar." },
            { "Glass/RainSource", "De onde vem a chuva no vidro: 0 = automático (usa o Nuclear Rain se estiver instalado, senão o próprio), 1 = próprio (só debaixo das nossas nuvens, com tempo ruim), 2 = Nuclear Rain (mesma conta do mod: chove onde ele chove)." },
            { "Glass/TestMode", "Teste: 0 = normal, 1 = nuvem (gotas), 2 = nuvem congelada (geada), 3 = chuva." },
            { "Atmosphere/NearRain", "Chuva caindo perto da câmera: 0 = automático (desliga se o Nuclear Rain estiver instalado), 1 = sempre, 2 = nunca." },
            { "Atmosphere/RainShafts", "Cortinas de chuva debaixo dos cúmulos-nimbos e cúmulos grossos (visíveis de longe)." },
            { "Atmosphere/Rainbow", "Arco-íris (principal + secundário) nas cortinas de chuva iluminadas. Raro de propósito: só com chuva de verdade, sol atrás de você e no ângulo certo. 0 = desliga." },
            { "Atmosphere/Glory", "Glória (anéis coloridos em volta do ponto oposto ao sol) e arco branco no topo de nuvens iluminadas logo abaixo de você. Raro de propósito. 0 = desliga." },
            { "Atmosphere/ValleyFog", "Névoa de vale nas montanhas: da noite até o meio da manhã, vento fraco, céu mais limpo e ar úmido. Enche os vales de verdade até um nível plano e some depois que o sol nasce. Rara de propósito. 0 = desliga." },
            { "Atmosphere/MountainCloud", "Nuvem de encosta: com ar úmido e vento, a nuvem gruda nas encostas do lado do vento e nos picos que chegam perto da base das nuvens; some do lado de trás. Rara de propósito. 0 = desliga." },
            { "Atmosphere/RainDensity", "Quanto a chuva tira a visibilidade (cortinas)." },
            { "Atmosphere/Lightning", "Relâmpagos nos cúmulos-nimbos (0 = desliga; maior = mais frequentes/fortes)." },
            { "Atmosphere/Turbulence", "Turbulência dentro das nuvens (0 = desliga). Cúmulo-nimbo sacode bem mais." },
            { "Atmosphere/GodRays", "Raios de sol entre as nuvens (0 = desliga)." },
            { "Atmosphere/LightShafts", "Feixes de luz pelos buracos das nuvens (volume no ar debaixo das nuvens; precisa das sombras ligadas). 0 = desliga." },
            { "Lighting/KeepSunBelowClouds", "O jogo apaga o sol abaixo da camada dele em tempo ruim; com isto ligado o sol continua e a sombra vem das nossas nuvens." },
            { "Atmosphere/Fog", "Névoa própria: substitui a névoa fake de céu fechado do jogo por uma que vem da umidade e da chuva do nosso tempo (só abaixo da base das nuvens). 0 = deixa a do jogo." },
            { "Atmosphere/MoonLight", "Brilho da lua nas nuvens à noite." }
        };
        static readonly Dictionary<string, string> PtTab = new Dictionary<string, string>
        {
            { "General", "Geral" },
            { "Layer", "Camada" },
            { "Shape", "Forma" },
            { "Scale", "Escala" },
            { "Variation", "Variação" },
            { "Wind", "Vento" },
            { "Weather", "Modelo" },
            { "Storm", "Tempestade" },
            { "Lighting", "Luz" },
            { "Quality", "Qualidade" },
            { "Shadows", "Sombra" },
            { "Glass", "Vidro" },
            { "Atmosphere", "Atmosfera" },
            { "Nuclear", "Nuclear" },
            { "Multiplayer", "Multijogador" }
        };
        bool Pt => _lang.Value == 2 || (_lang.Value == 0 && Application.systemLanguage == SystemLanguage.Portuguese);
        string T(string en, string pt) => Pt ? pt : en;

        // versions up to 0.11 used Portuguese section names: copy saved values into the new sections
        void MigrateSections()
        {
            try
            {
                var prop = typeof(ConfigFile).GetProperty("OrphanedEntries", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var orph = prop?.GetValue(Config) as Dictionary<ConfigDefinition, string>;
                if (orph == null || orph.Count == 0) return;
                int n = 0;
                foreach (var kv in PtTab)
                    foreach (var o in new List<ConfigDefinition>(orph.Keys))
                    {
                        if (o.Section != kv.Value) continue;
                        foreach (var e in Config)
                            if (e.Key.Section == kv.Key && e.Key.Key == o.Key) { e.Value.SetSerializedValue(orph[o]); n++; break; }
                        orph.Remove(o);
                    }
                if (n > 0) { Config.Save(); Log.LogInfo($"Config: {n} settings migrated from the old Portuguese sections."); }
            }
            catch (Exception e) { Log.LogWarning("Config migration failed: " + e.Message); }
        }

        void Awake()
        {
            Log = Logger;
            var C = Config;
            try
            {
                if (File.Exists(C.ConfigFilePath) && !File.ReadAllText(C.ConfigFilePath).Contains("ConfigVersion = 5"))
                { File.WriteAllText(C.ConfigFilePath, ""); C.Reload(); Log.LogInfo("Old config replaced with the current defaults."); }
            }
            catch (Exception e) { Log.LogWarning("Could not reset the old config: " + e.Message); }
            AcceptableValueBase R(float a, float b) => new AcceptableValueRange<float>(a, b);
            AcceptableValueBase Ri(int a, int b) => new AcceptableValueRange<int>(a, b);

            C.Bind("General", "ConfigVersion", 5, "Do not change: version of the defaults (the config is rebuilt when it changes).");
            _supercell = C.Bind("Storm", "Supercells", 0.35f, new ConfigDescription("Share of strong storms that become supercells: one huge rotating cell with a giant anvil, flanking line and wall cloud (0 = off).", R(0, 1)));
            _probeClouds = C.Bind("General", "CloudsInReflections", true, "Draw these clouds into the map's reflection probe, so the sea (and shiny surfaces) reflect them.");
            _waterRefl = C.Bind("General", "WaterReflection", 0, new ConfigDescription("Sea reflection when the mod draws the sea: 0 = auto (like the game), 1 = map reflection probe, 2 = plain sky.", Ri(0, 2)));
            _waterLight = C.Bind("General", "WaterLighting", true, "Gives the sea the game's ambient light and reflection when the mod draws it before the clouds (RenderOrder 0). Turn off if the sea looks wrong.");
            _maxCloudSpeed = C.Bind("Wind", "MaxCloudSpeed", 25f, new ConfigDescription("Top speed the clouds drift with the game wind (m/s). Storm gusts in the game can be very strong; the clouds follow a smooth average capped at this.", R(2, 80)));
            _lang = C.Bind("General", "Language", 0, new ConfigDescription("Menu language: 0 = automatic (system), 1 = English, 2 = Português.", Ri(0, 2)));
            _on = C.Bind("General", "Enabled", true, "Turns the volumetric clouds on.");
            _toggleKey = C.Bind("General", "ToggleKey", new KeyboardShortcut(KeyCode.F10), "Turns the clouds on/off.");
            _menuKey = C.Bind("General", "MenuKey", new KeyboardShortcut(KeyCode.F11), "Opens the live tuning menu.");
            _dumpKey = C.Bind("General", "DumpKey", new KeyboardShortcut(KeyCode.F10, KeyCode.LeftShift), "Writes a diagnostic to the log (camera, weather, URP).");
            _hideGame = C.Bind("General", "HideGameClouds", true, "Hides the game's own clouds so they don't double up: the particle clouds and the grey storm clouds painted in the sky (lightning and ground shadows stay).");
            _gameWeather = C.Bind("General", "FollowGameWeather", true, "Follow the mission weather: cloud layer height (mission editor), wind and the game's coverage.");
            _debug = C.Bind("General", "DebugView", false, "Diagnostic: paints cloud opacity red.");
            _jitter = C.Bind("General", "AnimatedJitter", true, "Changes the step noise every frame (less banding, a little grain).");
            _useDepth = C.Bind("General", "UseSceneDepth", true, "Cuts clouds behind terrain/aircraft using the scene depth.");
            _event = C.Bind("General", "RenderOrder", 0, new ConfigDescription("When to draw: 0 = before transparents (engine flames, smoke and explosions over the clouds; the sea is drawn by the mod before the clouds), 1 = after transparents (old).", Ri(0, 1)));

            _baseOffset = C.Bind("Layer", "BaseOffset", 0f, new ConfigDescription("Offset to the reference base (the mission cloud height). The real base of each region comes from humidity.", R(-3000, 3000)));
            _manualBase = C.Bind("Layer", "ManualBase", 1500f, new ConfigDescription("Reference base (m) when FollowGameWeather is off.", R(0, 15000)));
            _planetR = C.Bind("Layer", "PlanetRadius", 6371000f, new ConfigDescription("Radius used for horizon curvature (the map is flat; this only curves the layer far away).", R(500000, 50000000)));

            _coverage = C.Bind("Shape", "CoverageScale", 1f, new ConfigDescription("Multiplies the cumulus coverage computed by the weather model.", R(0, 2)));
            _density = C.Bind("Shape", "Density", 0.05f, new ConfigDescription("Extinction per metre inside the cloud.", R(0.002f, 0.2f)));
            _dome = C.Bind("Shape", "Dome", 0.45f, new ConfigDescription("How much the cloud narrows with height (higher = rounder, lower domes; lower = towers).", R(0, 1.5f)));
            _billow = C.Bind("Shape", "Billow", 0.45f, new ConfigDescription("Cauliflower billows on the cloud surface.", R(0, 1.5f)));
            _baseRough = C.Bind("Shape", "BaseRoughness", 0.5f, new ConfigDescription("Cumulus undersides: 0 = every cloud cut flat at the layer base; higher = each cloud a little higher (up to ~180 m), bottoms curving up toward the edges and softer.", R(0, 1)));
            _soft = C.Bind("Shape", "Softness", 0.14f, new ConfigDescription("Cloud edge (lower = sharper outline).", R(0.01f, 0.4f)));
            _erosion = C.Bind("Shape", "DetailErosion", 0.35f, new ConfigDescription("How much the fine noise eats from the edges (wisps at the base, cauliflower at the top).", R(0, 1)));
            _topDensity = C.Bind("Shape", "TopDensity", 1f, new ConfigDescription("Density at the top relative to the base.", R(0.3f, 4)));
            _shear = C.Bind("Shape", "WindShear", 300f, new ConfigDescription("Shear: the top is pushed downwind (x8 over 8 km of height).", R(0, 6000)));

            _perC = C.Bind("Scale", "ClusterPeriod", 30000f, new ConfigDescription("Size of cloud clusters (m).", R(5000, 300000)));
            _perCell = C.Bind("Scale", "CellPeriod", 9000f, new ConfigDescription("Size of each cloud (cell) (m). Smaller = smaller, more numerous clouds.", R(1000, 60000)));
            _perS = C.Bind("Scale", "BillowPeriod", 1600f, new ConfigDescription("Size of the billows (m).", R(200, 20000)));
            _perD = C.Bind("Scale", "DetailPeriod", 520f, new ConfigDescription("Size of the fine detail (m).", R(50, 5000)));
            _windMul = C.Bind("Scale", "GameWindMultiplier", 1f, new ConfigDescription("Multiplier of the game wind dragging the clouds.", R(0, 10)));
            _windSpeed = C.Bind("Scale", "WindSpeed", 8f, new ConfigDescription("Wind (m/s) when FollowGameWeather is off.", R(0, 60)));

            _blur = C.Bind("Shape", "Blur", 1f, new ConfigDescription("Light blur on the cloud image (pixels). 0 = off.", R(0, 4)));
            _farSoft = C.Bind("Shape", "FarSoftness", 1f, new ConfigDescription("Softer edges with distance (0 = same near and far).", R(0, 5)));
            _warp = C.Bind("Variation", "DomainWarp", 0.35f, new ConfigDescription("Distortion of the cloud map (breaks any grid/pattern).", R(0, 1.5f)));
            _perWarp = C.Bind("Variation", "WarpPeriod", 23000f, new ConfigDescription("Size of the distortion (m).", R(2000, 200000)));
            _turb = C.Bind("Variation", "Turbulence", 3500f, new ConfigDescription("3D turbulence that bends clouds with height (m).", R(0, 20000)));
            _perTurb = C.Bind("Variation", "TurbulencePeriod", 7000f, new ConfigDescription("Size of the turbulence (m).", R(1000, 50000)));
            _sizeMix = C.Bind("Variation", "SizeMix", 0.7f, new ConfigDescription("Amount of loose small/medium clouds (0 = only big ones, 0.3 = few small, 0.7 = default, 1 = many).", R(0, 1)));
            _stretch = C.Bind("Variation", "CloudStreets", 1.4f, new ConfigDescription("Stretch clouds along the wind (1 = round; 2+ = cloud streets).", R(1, 4)));

            _veer = C.Bind("Wind", "WindVeer", 0.5f, new ConfigDescription("How much the shear direction turns up to 8 km of height (rad).", R(-1.5f, 1.5f)));
            _evolve = C.Bind("Wind", "Evolution", 1.5f, new ConfigDescription("How fast the billows 'boil'/change shape (m/s). 0 = clouds only drift.", R(0, 10)));

            _condOv = C.Bind("Weather", "ConditionsOverride", -1f, new ConfigDescription("Weather: -1 = the mission's; 0 = fair-weather sky ... 1 = storm.", R(-1, 1)));
            _changeMin = C.Bind("Weather", "ChangeTime", 10f, new ConfigDescription("Minutes the cloud field takes to follow a weather change (conditions, base, front, day cycle). Real clouds never change at once.", R(1, 30)));
            _todOv = C.Bind("Weather", "TimeOfDayOverride", -1f, new ConfigDescription("Hour used by the day cycle: -1 = the mission's.", R(-1, 24)));
            _diurnal = C.Bind("Weather", "DiurnalCycle", true, new ConfigDescription("Day cycle: the sun heats the ground, cumulus grow in the afternoon and fade at night."));
            _humBias = C.Bind("Weather", "HumidityBias", 0f, new ConfigDescription("More/less humidity (lower base, more clouds, stratus).", R(-0.5f, 0.5f)));
            _insBias = C.Bind("Weather", "InstabilityBias", 0f, new ConfigDescription("More/less instability (taller clouds, storms).", R(-0.5f, 0.5f)));
            _deck = C.Bind("Layer", "OvercastDeck", 1f, new ConfigDescription("From overcast to storm, the mid layer becomes a sheet covering the whole sky (0 = off).", R(0, 1)));
            _baseVar = C.Bind("Weather", "BaseVariation", 2200f, new ConfigDescription("How much the base changes between humid and dry air (m).", R(0, 5000)));
            _perHum = C.Bind("Weather", "HumidityPeriod", 60000f, new ConfigDescription("Size of humidity regions (m).", R(10000, 400000)));
            _perIns = C.Bind("Weather", "InstabilityPeriod", 45000f, new ConfigDescription("Size of instability regions (m).", R(10000, 400000)));
            _skyCirrusCap = C.Bind("Weather", "GameCirrus", 0.55f, new ConfigDescription("How much weather the game's sky (its cirrus) may show: the game's 'conditions' are capped at this value in the sky. 0 = no game cirrus; above ~0.6 the game's grey patterned overcast comes back.", R(0, 1)));
            _ownCirrus = C.Bind("Weather", "OwnCirrus", false, "Draw the mod's own cirrus (off: the game's cirrus stays).");
            _ciTile = C.Bind("Weather", "CirrusScale", 40000f, new ConfigDescription("Size of the cirrus texture in the sky (m). Bigger = longer strands.", R(10000, 120000)));
            _cirrusAmt = C.Bind("Weather", "Cirrus", 1f, new ConfigDescription("Amount of cirrus (high wisps).", R(0, 2)));
            _midAmt = C.Bind("Weather", "MidLevel", 1f, new ConfigDescription("Amount of altocumulus/altostratus (mid layer).", R(0, 2)));
            _frontMode = C.Bind("Weather", "Front", 3, new ConfigDescription("Front: 0 none, 1 cold, 2 warm, 3 automatic (from the mission weather).", Ri(0, 3)));
            _frontDist = C.Bind("Weather", "FrontDistanceKm", 40f, new ConfigDescription("Where the front starts, upwind (km). It arrives with the wind.", R(-300, 300)));
            _frontWidth = C.Bind("Weather", "FrontWidthKm", 15f, new ConfigDescription("Width of the front band (km).", R(3, 60)));

            _cbTop = C.Bind("Storm", "CbTop", 10000f, new ConfigDescription("Top of cumulonimbus / anvils (m above sea level).", R(5000, 16000)));
            _perCb = C.Bind("Storm", "CbCellPeriod", 40000f, new ConfigDescription("Spacing between cumulonimbus (m).", R(10000, 200000)));
            _anvil = C.Bind("Storm", "Anvil", 0.3f, new ConfigDescription("Anvil width.", R(0, 0.8f)));
            _anvilShift = C.Bind("Storm", "AnvilDrift", 6000f, new ConfigDescription("How far the anvil stretches downwind (m).", R(0, 30000)));

            _stormDim = C.Bind("Lighting", "StormLight", 1f, new ConfigDescription("In storms, how much direct sun fades and light turns grey and diffuse (0 = like fair weather).", R(0, 1.5f)));
            _wetDark = C.Bind("Lighting", "WetDarkening", 1f, new ConfigDescription("Water-laden clouds (cumulonimbus, thick cumulus, overcast/rainy sky) get darker underneath and on the sides. 0 = off.", R(0, 2)));
            _shDark = C.Bind("Lighting", "ShadowDarkness", 0.6f, new ConfigDescription("How dark the shaded parts of the cloud get (away from the sun, base, cloud under cloud). 0 = light, 1 = very dark.", R(0, 1)));
            _absorb = C.Bind("Lighting", "Absorption", 1f, new ConfigDescription("Extinction multiplier along the view.", R(0.1f, 4)));
            _lightAbsorb = C.Bind("Lighting", "LightAbsorption", 0.8f, new ConfigDescription("Extinction on the way to the sun.", R(0.02f, 2)));
            _g1 = C.Bind("Lighting", "PhaseForward", 0.75f, new ConfigDescription("Forward scattering.", R(0, 0.95f)));
            _g2 = C.Bind("Lighting", "PhaseBack", -0.25f, new ConfigDescription("Back scattering.", R(-0.9f, 0)));
            _gMix = C.Bind("Lighting", "PhaseMix", 0.25f, new ConfigDescription("Mix of the two phases.", R(0, 1)));
            _powder = C.Bind("Lighting", "Powder", 0.6f, new ConfigDescription("Powder effect.", R(0, 1)));
            _powderK = C.Bind("Lighting", "PowderSharpness", 200f, new ConfigDescription("Powder sharpness.", R(1, 1000)));
            _sunMul = C.Bind("Lighting", "SunIntensity", 1.5f, new ConfigDescription("Multiplier of the game's sunlight.", R(0, 5)));
            _ambMul = C.Bind("Lighting", "AmbientIntensity", 1f, new ConfigDescription("Multiplier of the game's ambient (sky) light.", R(0, 3)));
            _exposure = C.Bind("Lighting", "Exposure", 1f, new ConfigDescription("Exposure.", R(0.2f, 6)));
            _tonemap = C.Bind("Lighting", "Tonemap", 2, new ConfigDescription("0 = never, 1 = always, 2 = automatic (only if the camera is not HDR).", Ri(0, 2)));
            _msA = C.Bind("Lighting", "MultiScatterEnergy", 0.5f, new ConfigDescription("Energy of each multiple-scattering octave.", R(0, 0.9f)));
            _msB = C.Bind("Lighting", "MultiScatterAttenuation", 0.3f, new ConfigDescription("Attenuation of each octave.", R(0.05f, 1)));
            _sunWhite = C.Bind("Lighting", "SunWhiten", 0.3737624f, new ConfigDescription("How much to whiten the sun colour.", R(0, 1)));
            _fogAmt = C.Bind("Lighting", "GameFog", 1f, new ConfigDescription("How much the game's fog applies to the clouds.", R(0, 2)));

            _preset = C.Bind("Quality", "Preset", "Custom", new ConfigDescription("Quality preset: Low, Medium, High or Ultra (Ultra = the full look, every pixel raymarched every frame). Choosing one sets Downscale, Temporal, Steps, LightSteps, MaxStep, MaxDistance and the shadow resolution/steps; changing any of those by hand turns this into Custom.", new AcceptableValueList<string>(PresetNames)));
            _steps = C.Bind("Quality", "Steps", 256, new ConfigDescription("Maximum raymarch steps.", Ri(16, 512)));
            _lightSteps = C.Bind("Quality", "LightSteps", 4, new ConfigDescription("Steps toward the sun.", Ri(1, 6)));
            _downscale = C.Bind("Quality", "Downscale", 2, new ConfigDescription("1 = full resolution, 2 = half, 4 = quarter.", Ri(1, 4)));
            _temporal = C.Bind("Quality", "Temporal", 0, new ConfigDescription("Temporal reprojection: 0 = off (every cloud pixel raymarched every frame), 1 = half of the pixels per frame (checkerboard), 2 = a quarter per frame (2x2). The rest comes from the previous frames, moved with the camera and kept within the colours of the fresh pixels around them. Lightning, nuclear flashes and camera cuts are always drawn in full.", Ri(0, 2)));
            _minStep = C.Bind("Quality", "MinStep", 15f, new ConfigDescription("Minimum step (m).", R(5, 200)));
            _stepGrowth = C.Bind("Quality", "StepGrowth", 0.006f, new ConfigDescription("How much the step grows with distance.", R(0.001f, 0.05f)));
            _maxStep = C.Bind("Quality", "MaxStep", 300f, new ConfigDescription("Maximum step (m).", R(50, 3000)));
            _maxDist = C.Bind("Quality", "MaxDistance", 80000f, new ConfigDescription("Maximum drawn distance (m).", R(10000, 400000)));
            _mapSlicing = C.Bind("Quality", "MapSlicing", true, "Weather maps stay put and are refreshed one per frame (recentered only after the camera moves ~1 km); the reflection probe reuses them. Same look, less GPU. Off = rebuild all of them every frame, as in 1.1.");
            _shEvery = C.Bind("Quality", "ShadowUpdateEvery", 2, new ConfigDescription("Cloud shadow maps are redrawn every N frames (the cookie and the opacity depth map take turns). 1 = every frame, as in 1.1.", Ri(1, 4)));
            _perfMeter = C.Bind("Quality", "PerformanceMeter", false, "Shows how many milliseconds each part of the mod takes (in this tab). Costs almost nothing, but leave it off when not looking.");
            _farSteps = C.Bind("Shadows", "CloudOnCloudSteps", 4, new ConfigDescription("Long steps toward the sun (shadow of one cloud on another, up to ~7 km). 0 = off.", Ri(0, 6)));
            _farShadow = C.Bind("Shadows", "CloudOnCloudStrength", 0.6f, new ConfigDescription("Strength of cloud-on-cloud shadow.", R(0, 2)));
            _shOn = C.Bind("Shadows", "Shadows", true, "Cloud shadows on the ground and aircraft (replaces the game's sun cookie).");
            _shDist = C.Bind("Shadows", "ShadowDistance", 30000f, new ConfigDescription("Size of the shadowed area around the camera (m).", R(5000, 80000)));
            _shRes = C.Bind("Shadows", "ShadowResolution", 512, new ConfigDescription("Shadow map resolution.", Ri(128, 2048)));
            _shOpacity = C.Bind("Shadows", "ShadowOpacity", 0.85f, new ConfigDescription("How much the shadow darkens sunlight.", R(0, 1)));
            _shSteps = C.Bind("Shadows", "ShadowSteps", 16, new ConfigDescription("Steps through the cloud layer.", Ri(4, 48)));
            InitPresets();
            _shProt = C.Bind("Shadows", "OwnAircraftRadius", 150f, new ConfigDescription("Radius (m) around the camera where clouds below it don't shadow (protects your aircraft). The rest of the ground gets shadow from all clouds.", R(0, 2000)));
            _shBelow = C.Bind("Shadows", "ShadowFromBelow", 0f, new ConfigDescription("1 = clouds below the camera shadow even your aircraft (normally 0).", R(0, 1)));

            _wgOn = C.Bind("Glass", "WetGlass", true, "Droplets on the canopy inside cloud/rain and frost above the freezing altitude.");
            _wgInt = C.Bind("Glass", "Intensity", 1f, new ConfigDescription("Amount of water/ice on the glass.", R(0, 3)));
            _wgFreeze = C.Bind("Glass", "FreezingAltitude", 4500f, new ConfigDescription("Altitude (m) above which cloud freezes on the glass (frost instead of droplets).", R(0, 12000)));
            _wgRainTau = C.Bind("Glass", "RainThickness", 150f, new ConfigDescription("How thick the cloud above must be to rain on you (lower = rains more easily; 0 = no rain).", R(0, 1000)));
            _wgFrostReach = C.Bind("Glass", "FrostReach", 1f, new ConfigDescription("How far frost creeps from the edges toward the middle of the glass.", R(0, 2)));
            _wgFrostWhite = C.Bind("Glass", "FrostWhite", 0.8f, new ConfigDescription("How white/opaque the frost is.", R(0, 1)));
            _wgRefract = C.Bind("Glass", "Refraction", 0.035f, new ConfigDescription("How much droplets distort the image.", R(0, 0.15f)));
            _wgSize = C.Bind("Glass", "DropSize", 0.5f, new ConfigDescription("Droplet size.", R(0.15f, 1.5f)));
            _wgAir = C.Bind("Glass", "Airflow", 2.5f, new ConfigDescription("Airflow strength on the glass: higher = droplets turn into streaks sooner/at lower speed.", R(0.3f, 8)));
            _wgRainSrc = C.Bind("Glass", "RainSource", 0, new ConfigDescription("Where glass rain comes from: 0 = automatic (uses Nuclear Rain if installed, otherwise our own), 1 = own (only under our clouds in bad weather), 2 = Nuclear Rain (same formula as that mod: rains where it rains).", Ri(0, 2)));
            _wgTest = C.Bind("Glass", "TestMode", 0, new ConfigDescription("Test: 0 = normal, 1 = cloud (droplets), 2 = frozen cloud (frost), 3 = rain.", Ri(0, 3)));

            if (!LoadBundle()) return;
            _atNearRain = C.Bind("Atmosphere", "NearRain", 0, new ConfigDescription("Rain falling near the camera: 0 = automatic (off if Nuclear Rain is installed), 1 = always, 2 = never.", Ri(0, 2)));
            _atShafts = C.Bind("Atmosphere", "RainShafts", true, "Rain curtains under cumulonimbus (visible from afar).");
            _atRainVis = C.Bind("Atmosphere", "RainDensity", 1f, new ConfigDescription("How much rain reduces visibility (curtains).", R(0.2f, 3)));
            _atRainbow = C.Bind("Atmosphere", "Rainbow", 1f, new ConfigDescription("Rainbow (primary + secondary bow) in sunlit rain curtains. Rare on purpose: only real rain, lit by a sun behind you, at the right angle. 0 = off.", R(0, 2)));
            _atGlory = C.Bind("Atmosphere", "Glory", 1f, new ConfigDescription("Glory (coloured rings around the antisolar point) and white cloudbow on sunlit cloud tops close below you. Rare on purpose. 0 = off.", R(0, 2)));
            _atLightning = C.Bind("Atmosphere", "Lightning", 1f, new ConfigDescription("Lightning in cumulonimbus (0 = off; higher = more frequent/stronger).", R(0, 3)));
            _atTurb = C.Bind("Atmosphere", "Turbulence", 1f, new ConfigDescription("Turbulence inside clouds (0 = off). Cumulonimbus shake much more.", R(0, 3)));
            _atRays = C.Bind("Atmosphere", "GodRays", 0.35f, new ConfigDescription("Screen-space sun rays between clouds (0 = off).", R(0, 1.5f)));
            _atShafts2 = C.Bind("Atmosphere", "LightShafts", 0.5f, new ConfigDescription("Light shafts through gaps in the clouds (volume in the air below the clouds; needs shadows on). 0 = off.", R(0, 2)));
            _keepSun = C.Bind("Lighting", "KeepSunBelowClouds", true, "The game switches the sun off below its cloud layer in bad weather; with this on the sun stays and shadow comes from our clouds.");
            _atFog = C.Bind("Atmosphere", "Fog", 1f, new ConfigDescription("Own fog: replaces the game's fake overcast fog with fog from humidity and rain of our weather (only below the cloud base). 0 = keep the game's.", R(0, 3)));
            _atMoon = C.Bind("Atmosphere", "MoonLight", 1f, new ConfigDescription("Moonlight brightness on clouds at night.", R(0, 4)));
            MigrateSections();
            Wet = new NOCWetGlass(this);
            if (Wet.Init(_ab)) _gpass = new WetGlassPass(Wet); else Wet = null;
            _pass = new CloudPass(this);
            _wpass = new WaterPass(this);
            _spass = new ShadowPass(this);
            Atmos = new NOCAtmos(this); Atmos.Init(_ab); _apass = new AtmosPass(this);
            Nukes = new NOCNukes(this, C); Nukes.Patch();
            Sync = new NOCSync(this, C);
            Boscali = new NOCBoscali(C);
            Oro = new NOCMountainFog(C);
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
            NoAtmoFXApi.P = this; _onPrev = _on.Value;
            Log.LogInfo("NoAtmoFX " + Version + " loaded (integration API " + NoAtmoFXApi.ApiVersion + ").");
        }

        bool LoadBundle()
        {
            string path = Path.Combine(Path.GetDirectoryName(Info.Location), "noatmofx");
            if (!File.Exists(path)) { Log.LogError("Bundle not found: " + path); return false; }
            var ab = _ab = AssetBundle.LoadFromFile(path);
            if (ab == null) { Log.LogError("Could not open the bundle."); return false; }
            foreach (var n in ab.GetAllAssetNames())
                if (n.EndsWith(".mat")) _mat = ab.LoadAsset<Material>(n);
            if (_mat == null || _mat.shader == null || !_mat.shader.isSupported) { Log.LogError("Cloud material/shader is invalid or not supported."); return false; }
            Log.LogInfo($"Shader '{_mat.shader.name}' ok, {_mat.shader.passCount} passes.");
            return true;
        }

        // ---------- water: the game's sea is transparent and would be drawn after the clouds, so the mod draws it first ----------
        static readonly FieldInfo F_waterPlane = typeof(LevelInfo).GetField("waterPlane", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        internal readonly List<Renderer> Water = new List<Renderer>();
        readonly Dictionary<Renderer, uint> _waterMask = new Dictionary<Renderer, uint>();
        internal bool WaterOwned;
        void SetWaterOwned(bool own)
        {
            if (own && Water.Count == 0 && _level != null && F_waterPlane != null)
            {
                var t = F_waterPlane.GetValue(_level) as Transform;
                // transparent surface only: 'oceanUnderlay' is opaque (URP/Lit, queue 2000) and MUST stay in the game's
                // opaque pass, otherwise it is missing from the color/depth copy that WaterSurface reads (waves, depth color)
                if (t != null) { foreach (var r0 in t.GetComponentsInChildren<Renderer>(true)) { bool tr = false; foreach (var mm in r0.sharedMaterials) if (mm != null && mm.renderQueue > 2500) tr = true; if (tr) Water.Add(r0); } Log.LogInfo("Water: " + Water.Count + " renderer(s) in " + t.name + " drawn before the clouds."); }
            }
            if (own && Water.Count == 0) own = false;
            if (own == WaterOwned) return;
            WaterOwned = own;
            foreach (var r in Water)
            {
                if (r == null) continue;
                if (own) { if (!_waterMask.ContainsKey(r)) _waterMask[r] = r.renderingLayerMask; r.renderingLayerMask = 0; }
                else if (_waterMask.TryGetValue(r, out var m)) r.renderingLayerMask = m;
            }
            if (!own)
            {
                _waterMask.Clear();
                foreach (var kv in _waterLpu) if (kv.Key != null) { kv.Key.lightProbeUsage = kv.Value; kv.Key.SetPropertyBlock(null); }
                _waterLpu.Clear();
            }
        }
        // drawn by hand, the sea gets neither ambient light (SH) nor the game's reflection probe and looks dark:
        // pass both through the renderer's own property block
        static readonly FieldInfo F_reflProbe = typeof(LevelInfo).GetField("reflectionProbe", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        static readonly int ID_Spec0 = Shader.PropertyToID("unity_SpecCube0"), ID_Spec0HDR = Shader.PropertyToID("unity_SpecCube0_HDR"), ID_LightData = Shader.PropertyToID("unity_LightData");
        MaterialPropertyBlock _wmpb; readonly SphericalHarmonicsL2[] _wsh = new SphericalHarmonicsL2[1]; readonly Vector4[] _wocc = { Vector4.one };
        readonly Dictionary<Renderer, UnityEngine.Rendering.ReflectionProbeUsage> _waterLpuRefl = new Dictionary<Renderer, UnityEngine.Rendering.ReflectionProbeUsage>();
        readonly Dictionary<Renderer, UnityEngine.Rendering.LightProbeUsage> _waterLpu = new Dictionary<Renderer, UnityEngine.Rendering.LightProbeUsage>();
        void WaterLighting(Renderer r)
        {
            if (_wmpb == null) _wmpb = new MaterialPropertyBlock();
            if (!_waterLpu.ContainsKey(r))
            {
                _waterLpuRefl[r] = r.reflectionProbeUsage;
                var sb = new System.Text.StringBuilder($"Water '{r.name}': reflectionProbeUsage {r.reflectionProbeUsage}, lightProbeUsage {r.lightProbeUsage}, probeAnchor {(r.probeAnchor ? r.probeAnchor.name : "-")}");
                foreach (var mm in r.sharedMaterials) if (mm != null) sb.Append($" | mat {mm.name} shader {mm.shader.name} queue {mm.renderQueue} kw [{string.Join(",", mm.shaderKeywords)}]");
                var pr0 = _level != null ? F_reflProbe?.GetValue(_level) as ReflectionProbe : null;
                if (pr0 != null) sb.Append($" | probe {pr0.name} mode {pr0.mode} size {pr0.size} pos {pr0.transform.position} tex {(pr0.texture ? pr0.texture.name : "null")}");
                sb.Append($" | defaultReflection {RenderSettings.defaultReflectionMode} custom {(RenderSettings.customReflectionTexture ? RenderSettings.customReflectionTexture.name : "null")}");
                Log.LogInfo(sb.ToString());
            }
            if (!_waterLpu.ContainsKey(r)) { _waterLpu[r] = r.lightProbeUsage; r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.CustomProvided; }
            r.GetPropertyBlock(_wmpb);
            _wsh[0] = RenderSettings.ambientProbe;
            _wmpb.CopySHCoefficientArraysFrom(_wsh);
            _wmpb.CopyProbeOcclusionArrayFrom(_wocc);
            // reflection: 0 auto (same as the game: the renderer says whether it uses a probe), 1 map probe, 2 default sky
            int wm = _waterRefl.Value;
            bool useProbe = wm == 1 || (wm == 0 && _waterLpuRefl.TryGetValue(r, out var ru) && ru != UnityEngine.Rendering.ReflectionProbeUsage.Off);
            var pr = useProbe && _level != null ? F_reflProbe?.GetValue(_level) as ReflectionProbe : null;
            Texture cube = pr != null ? (pr.texture != null ? pr.texture : pr.realtimeTexture) : null;
            if (cube != null) { _wmpb.SetTexture(ID_Spec0, cube); _wmpb.SetVector(ID_Spec0HDR, pr.textureHDRDecodeValues); }
            else if (ReflectionProbe.defaultTexture != null) { _wmpb.SetTexture(ID_Spec0, ReflectionProbe.defaultTexture); _wmpb.SetVector(ID_Spec0HDR, ReflectionProbe.defaultTextureHDRDecodeValues); }
            // unity_LightData.z = 1: URP multiplies the sun light by this value (per-object culling mask).
            // cmd.DrawRenderer does not fill it in and it came through as 0: no sun on the sea (no glint, no diffuse, flat dark sea)
            _wmpb.SetVector(ID_LightData, new Vector4(0, 0, 1, 0));
            r.SetPropertyBlock(_wmpb);
        }
        internal void DrawWater(CommandBuffer cmd)
        {
            foreach (var r in Water)
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (_waterLight.Value) WaterLighting(r);
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i]; if (m == null) continue;
                    int ps = m.FindPass("UniversalForward"); if (ps < 0) ps = m.FindPass("SRPDefaultUnlit"); if (ps < 0) ps = 0;
                    cmd.DrawRenderer(r, m, i, ps);
                }
            }
        }

        // ---------- game sky: the skybox draws its own storm clouds based on _Conditions ----------
        static readonly int ID_SkyCond = Shader.PropertyToID("_Conditions"), ID_SkyOcc = Shader.PropertyToID("_CloudOcclusion");
        Material _skyLogged;
        void OverrideSkyClouds()
        {
            if (!_on.Value || !_hideGame.Value || _level == null) return;
            var sky = RenderSettings.skybox;
            if (sky == null) return;
            if (sky != _skyLogged)
            {
                _skyLogged = sky;
                var sb = new StringBuilder("Game sky: material " + sky.name + ", shader " + (sky.shader != null ? sky.shader.name : "?") + ". Properties:");
                var sh = sky.shader;
                if (sh != null) for (int i = 0; i < sh.GetPropertyCount(); i++) sb.Append(' ').Append(sh.GetPropertyName(i)).Append('(').Append(sh.GetPropertyType(i)).Append(')');
                Log.LogInfo(sb.ToString());
            }
            // the game sky draws the cirrus AND the patterned grey overcast from _Conditions: zeroing it removed the cirrus
            // too. Pass the real weather only up to a cap (cirrus stays, the bad-weather sheet does not)
            if (sky.HasProperty(ID_SkyCond)) sky.SetFloat(ID_SkyCond, Mathf.Min(_level.conditions, _skyCirrusCap.Value));
            if (sky.HasProperty(ID_SkyOcc)) sky.SetFloat(ID_SkyOcc, 0f);
        }
        void LateUpdate() { OverrideSkyClouds(); KeepSun(); OwnFog(); }

        // ---------- own fog ----------
        // Below its cloud layer in bad weather the game adds +0.001 density (~3 km visibility) everywhere,
        // unrelated to our clouds. Here: the game's "clean" fog (altitude/map haze) + humidity
        // near saturation + real rain at the camera (probe), only below the base, smoothed over time.
        float _fogWritten = -1, _fogBase, _fogRain, _fogHum, _fogBelow; Color _fogColWritten, _fogColBase;
        internal float FogExtra;
        void OwnFog()
        {
            if (!_on.Value || _atFog.Value <= 0.001f || _level == null || (Boscali != null && Boscali.YieldFog)) { _fogWritten = -1; FogExtra = 0; return; }
            var cam = TargetCamera(); if (cam == null) return;
            // the game only rewrites the fog now and then: if the value is still ours, the stored base is valid
            if (Mathf.Abs(RenderSettings.fogDensity - _fogWritten) > 1e-9f)
            {
                float g = RenderSettings.fogDensity;
                if (_cloudLayer != null && _level.conditions > 0.6f)
                {
                    float n3 = Mathf.Clamp01((_cloudLayer.transform.position.y + 200f - cam.transform.position.y) * 0.005f);
                    float n4 = n3 * Mathf.InverseLerp(0.8f, 1f, _level.conditions);
                    g = Mathf.Max(g - n4 * 0.001f, 3e-5f);
                }
                _fogBase = g;
            }
            if (RenderSettings.fogColor != _fogColWritten) _fogColBase = RenderSettings.fogColor;
            float dt = Time.deltaTime, k = 1f - Mathf.Exp(-dt / 8f);
            float rain = (Atmos != null && Atmos.ProbeRainValid) ? Mathf.Clamp01(Atmos.ProbeRain * 1.4f) : 0f;
            var nrp = _wgRainSrc.Value == 1 ? null : NuclearRain();
            if (nrp != null) rain = Mathf.Max(rain, RainNow(out _));
            float hum = Mathf.InverseLerp(0.7f, 0.95f, _condS);
            if (_menu || Time.timeSinceLevelLoad < 2f) k = 1f;
            _fogRain += (rain - _fogRain) * k; _fogHum += (hum - _fogHum) * k;
            // rain/humidity fog at ground level (3/visibility): humid ~15 km, heavy rain ~1.8 km.
            // Goes to the height fog pass (14); Unity fog keeps only the map's clean fog.
            FogExtra = (_fogHum * 3f / 15000f + Mathf.Pow(_fogRain, 0.8f) * 3f / 2000f) * _atFog.Value;
            RenderSettings.fogDensity = _fogBase; _fogWritten = _fogBase;
            RenderSettings.fogColor = _fogColBase; _fogColWritten = _fogColBase;
        }

        // below its layer in bad weather the game turns the sun off (light + disc); with our clouds the shadow
        // comes from their shadow map, so the sun stays on
        static readonly FieldInfo F_sunMat = typeof(LevelInfo).GetField("sunMat", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        static readonly int ID_Emis = Shader.PropertyToID("_EmissionColor");
        void KeepSun()
        {
            if (!_on.Value || !_keepSun.Value || _level == null) return;
            var sun = _level.sun;
            if (sun == null || sun.color.r <= 0f || sun.gameObject.activeSelf) return;
            sun.gameObject.SetActive(true);
            if (_level.sunObject != null) _level.sunObject.SetActive(true);
            var sm = F_sunMat?.GetValue(_level) as Material;
            if (sm != null)
            {
                float n6 = 1f - Mathf.Abs(Vector3.Dot(sun.transform.forward, Vector3.up));
                sm.SetColor(ID_Emis, sun.color * 1000000f * Mathf.Clamp01(1f - n6));
            }
        }

        void OnDestroy()
        {
            SetWaterOwned(false);
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            SetGameCloudsHidden(false);
            Wet?.Release(); if (ProbeRT != null) ProbeRT.Release(); Atmos?.Release();
            foreach (var r in new[] { CloudRT, ReflCloudRT, WxA, WxB, WxC, WxP, ShadowRT, ShadowDRT, TFresh, THist0, THist1 }) if (r != null) r.Release();
        }

        // ---------- rain: our own, or matched to the Nuclear Rain mod ----------
        BaseUnityPlugin _nrPlugin; bool _nrChecked;
        BaseUnityPlugin NuclearRain()
        {
            if (!_nrChecked)
            {
                _nrChecked = true;
                if (BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue("neutral.rain", out var info) && info.Instance != null)
                {
                    _nrPlugin = info.Instance;
                    Log.LogInfo("Wet glass: Nuclear Rain found, rain on the glass follows it (Glass > RainSource = 0/2).");
                }
            }
            return _nrPlugin;
        }
        static T NrCfg<T>(BaseUnityPlugin p, string sec, string key, T def)
        {
            try { if (p.Config.TryGetEntry<T>(sec, key, out var e)) return e.Value; } catch { }
            return def;
        }
        float RainNow(out bool needCloud)
        {
            needCloud = false;
            var lvl = _level; if (lvl == null) return 0;
            var cam = Camera.main; if (cam == null) return 0;
            float camAlt = cam.transform.position.y - Datum.LocalSeaY;
            int src = _wgRainSrc.Value;
            var nr = src == 1 ? null : NuclearRain();
            if (nr != null)
            {
                // same math as Nuclear Rain: below the game's base, conditions above 0.6
                if (NrCfg(nr, "Rain", "UseSnow", false)) return 0;   // snow: no water on the glass
                float fade = Mathf.Max(1f, NrCfg(nr, "Rain", "FadeBand", 300f));
                float below = Mathf.Clamp01((lvl.cloudHeight - camAlt) / fade);
                return below * Mathf.Clamp01((Mathf.Clamp01(lvl.conditions) - 0.6f) / 0.4f);
            }
            if (src == 2) return 0;   // asked for Nuclear Rain but it is not installed
            // own: bad weather and below our cloud base; only drips with cloud overhead (probe)
            needCloud = true;
            return Mathf.InverseLerp(0.55f, 0.85f, _condS) * Mathf.Clamp01((_effBottom + 200f - camAlt) / 400f);
        }

        // ---------- camera ----------
        Camera TargetCamera()
        {
            var csm = SceneSingleton<CameraStateManager>.i;
            if (csm != null && csm.mainCamera != null) return csm.mainCamera;
            return null;
        }

        void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
        {
            // wet glass: on the camera that draws the cockpit (the cockpit camera if there is one, otherwise the main one)
            if (_gpass != null && Wet.Active && cam == Wet.CockpitCam())
            {
                var gd = cam.GetUniversalAdditionalCameraData();
                if (gd != null && gd.scriptableRenderer != null) gd.scriptableRenderer.EnqueuePass(_gpass);
            }
            // map reflection probe: our clouds go into it too (otherwise the sea reflects a sky with no clouds
            // at all, since the game's are hidden, and looks too dark blue)
            if (_on.Value && _level != null && cam.cameraType == CameraType.Reflection && _probeClouds.Value)
            {
                var pd = cam.GetUniversalAdditionalCameraData();
                if (pd != null && pd.scriptableRenderer != null) { _pass.renderPassEvent = RenderPassEvent.BeforeRenderingTransparents; pd.scriptableRenderer.EnqueuePass(_pass); }
                return;
            }
            if (!_on.Value || cam.cameraType != CameraType.Game || cam != TargetCamera() || _level == null) return;
            if (!_camData.TryGetValue(cam, out var data) || data == null)
            {
                data = cam.GetUniversalAdditionalCameraData();
                _camData[cam] = data;
            }
            if (data == null || data.scriptableRenderer == null) return;
            OverrideSkyClouds();
            // clouds before transparents (smoke/explosions sort correctly); where the ray hits water,
            // the water is transparent and drawn later, so the composite is redone after it there
            _pass.renderPassEvent = _event.Value == 0 ? RenderPassEvent.BeforeRenderingTransparents : RenderPassEvent.AfterRenderingTransparents;
            data.scriptableRenderer.EnqueuePass(_pass);
            if (_event.Value == 0 && !WaterOwned) data.scriptableRenderer.EnqueuePass(_wpass);
            if (_shOn.Value) data.scriptableRenderer.EnqueuePass(_spass);
            data.scriptableRenderer.EnqueuePass(_apass);
        }

        // ---------- atmosphere ----------
        bool NearRainOn() => _atNearRain.Value == 1 || (_atNearRain.Value == 0 && NuclearRain() == null && !(Boscali != null && Boscali.YieldNearRain));
        internal void DrawNearRain(CommandBuffer cmd, Camera cam)
        {
            if (Atmos == null || !NearRainOn() || _level == null) return;
            // only under an actual cloud: the rain field leans with the wind, so without this drops could fall
            // out of a clear sky a few km downwind of a shower
            float amt = Atmos.RainHere * (ProbeAtValid ? Mathf.Clamp01((ProbeTauAt - 1.5f) / 4f) : 0f);
            if (amt < 0.01f) return;
            var csm = SceneSingleton<CameraStateManager>.i;
            Vector3 camVel = csm != null ? csm.cameraVelocity : Vector3.zero;
            Color amb = RenderSettings.ambientSkyColor * RenderSettings.ambientIntensity;
            Light sun = _level.sun;
            Color sc = sun != null && sun.isActiveAndEnabled ? sun.color * sun.intensity : Color.black;
            // drops are dark, like the base of a rain cloud (they used to be bright and showed up as white dots)
            Color lc = amb * 0.16f + sc * 0.005f; lc.a = 1;
            Atmos.DrawRain(cmd, cam, amt, camVel, _level.windVelocity, lc);
        }
        void FixedUpdate()
        {
            if (Atmos == null || _level == null || !_on.Value) return;
            Atmos.Fixed(Time.fixedDeltaTime, ProbeAtValid ? ProbeCloudAt : 0f, Atmos.ProbeCb, _atTurb.Value);
        }

        // ---------- per frame ----------
        void Update()
        {
            Perf.SetEnabled(_perfMeter.Value);
            Perf.Tick();
            BenchTick();
            if (_toggleKey.Value.IsDown()) { _on.Value = !_on.Value; Log.LogInfo("Clouds " + (_on.Value ? "ON" : "OFF")); }
            if (_on.Value != _onPrev) { _onPrev = _on.Value; NoAtmoFXApi.RaiseEnabled(_onPrev); }
            if (_dumpKey.Value.IsDown()) Dump();
            if (_menuKey.Value.IsDown())
            {
                _menu = !_menu;
                if (_menu) { _prevLock = Cursor.lockState; _prevVis = Cursor.visible; Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
                else { Cursor.lockState = _prevLock; Cursor.visible = _prevVis; SaveConfig(); }
            }
            if (_menu) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }

            var lvl = NetworkSceneSingleton<LevelInfo>.i;
            if (lvl != _level)
            {
                SetWaterOwned(false); Water.Clear();
                _level = lvl; _wxInit = false; _covInit = false; _windDirSet = false; _frontInit = false; _cloudLayer = null; _weatherSet = null; _hidden.Clear(); _camData.Clear();
                if (_level != null && F_cloudLayer != null) _cloudLayer = F_cloudLayer.GetValue(_level) as Component;
                if (_cloudLayer != null && F_ws == null) F_ws = _cloudLayer.GetType().GetField("currentWeatherSet", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            }
            Boscali?.Tick();
            Sync?.Tick(Time.deltaTime, _level != null);
            if (_level == null) return;

            SetGameCloudsHidden(_on.Value && _hideGame.Value);
            SetWaterOwned(_on.Value && _event.Value == 0);

            // weather
            float dt = Time.deltaTime;
            // multiplayer: drift and evolution run on the mission clock shared by the game, so every player
            // (also one who joins late) has the same clouds; the F11 menu does not snap the weather there
            bool mp = Sync != null && Sync.Shared;
            float dts = mp ? (float)Sync.DT : dt;
            bool snapMenu = _menu && !mp;
            bool reanchor = mp && (!_wxInit || Sync.Jumped);   // start of the mission clock: drift as if this wind blew since then
            if (reanchor) _evolveT = _evolve.Value * Sync.T; else _evolveT += _evolve.Value * dts;
            if (Wet != null)
            {
                Wet.DropSize = _wgSize.Value; Wet.Airflow = _wgAir.Value;

            }
            {
                // rain here: Nuclear Rain (if it is the source) or our own clouds' rain field (GPU probe)
                float rainHere = (Atmos != null && Atmos.ProbeRainValid) ? Mathf.Clamp01(Atmos.ProbeRain * 1.4f) : 0f;
                var nrp = _wgRainSrc.Value == 1 ? null : NuclearRain();
                float rainGlass = nrp != null ? RainNow(out _) : rainHere;
                if (Wet != null) { Wet.GameRain = rainGlass; Wet.RainNeedsCloud = false; }
                bool wl = _weatherSet is WeatherSet wsl && wsl != null && wsl.lightning;
                Atmos?.Step(dt, TargetCamera(), _level, Mathf.Max(_cbTop.Value, 9300f), _on.Value ? _atLightning.Value * (float.IsNaN(NoAtmoFXApi.Effective.Lightning) ? 1f : NoAtmoFXApi.Effective.Lightning) : 0f, wl, rainHere);
            }
            Wet?.Step(dt, _wgOn.Value, _wgInt.Value, _wgFreeze.Value, _wgRainTau.Value, _wgTest.Value, _wgFrostReach.Value, _wgFrostWhite.Value, _wgRefract.Value, _level.sun);
            float gameCov = -1;
            if (_cloudLayer != null && F_ws != null)
            {
                _weatherSet = F_ws.GetValue(_cloudLayer);
                if (_weatherSet is WeatherSet ws && ws != null) gameCov = ws.coverage;
            }
            var ext = NoAtmoFXApi.Effective;   // weather from other mods (or the host's, in multiplayer)
            _extSrc = ext.Any;
            if (_gameWeather.Value || ext.Any)
            {
                _effBottom = !float.IsNaN(ext.Base) ? ext.Base + _baseOffset.Value : _gameWeather.Value ? _level.cloudHeight + _baseOffset.Value : _manualBase.Value;
                // the game's wind gusts and can reach huge values in storms: clouds follow a smooth
                // average (~20 s) with a speed cap, and a stalled frame does not turn into a jump
                Vector3 wv = ext.HasWind ? new Vector3(ext.WindX, 0, ext.WindZ) : _level.windVelocity * _windMul.Value; wv.y = 0;
                if (!ext.HasWind && !_gameWeather.Value) wv = new Vector3(0.94f, 0, 0.34f) * _windSpeed.Value;
                if (wv.magnitude > _maxCloudSpeed.Value) wv = wv.normalized * _maxCloudSpeed.Value;
                float kw = 1f - Mathf.Exp(-dt / 120f);   // drift changes heading/speed over ~2 min
                if (mp) kw = 1f - Mathf.Exp(-dts / 120f);
                if (!_wxInit || snapMenu) _windS = wv; else _windS += (wv - _windS) * kw;
                if (reanchor) { _windX = _windS.x * Sync.T; _windZ = _windS.z * Sync.T; }   // as if this wind blew since the start
                else
                {
                    float dtw = mp ? dts : Mathf.Min(dt, 0.1f);
                    _windX += _windS.x * dtw;
                    _windZ += _windS.z * dtw;
                }
                if (!ext.HasWind && _gameWeather.Value && Time.time - _windLogT > 30f && _level.windVelocity.magnitude * _windMul.Value > _maxCloudSpeed.Value)
                { _windLogT = Time.time; Log.LogInfo($"Wind: game {_level.windVelocity.magnitude:F1} m/s, clouds capped at {_maxCloudSpeed.Value:F0} m/s."); }
            }
            else
            {
                _effBottom = _manualBase.Value;
                if (reanchor) { _windX = 0.94 * _windSpeed.Value * Sync.T; _windZ = 0.34 * _windSpeed.Value * Sync.T; }
                else { _windX += 0.94 * _windSpeed.Value * dts; _windZ += 0.34 * _windSpeed.Value * dts; }
            }
            _effBottom = Mathf.Max(0, _effBottom);
            Nukes?.Step(dt, _level, TargetCamera(), _windX, _windZ);
            // game base and conditions also change slowly (the game can switch all at once)
            // change time: everything that changes the cloud field follows the weather slowly
            float Tc = Mathf.Max(12f, _changeMin.Value * 12f);   // two smoothing stages in series: ~95% at ChangeTime
            float condRaw = _condOv.Value >= 0 ? _condOv.Value : !float.IsNaN(ext.Conditions) ? ext.Conditions : _level.conditions;
            if (!_wxInit || snapMenu) { _condS = condRaw; _baseS = _effBottom; }
            else { float k0 = 1f - Mathf.Exp(-dts / Tc); _condS += (condRaw - _condS) * k0; _baseS += (_effBottom - _baseS) * k0; }
            _effBottom = _baseS;

            // ---- weather model: mission weather + time of day -> humidity (H) and instability (I) ----
            _cond = _condS;
            _tod = _todOv.Value >= 0 ? _todOv.Value : !float.IsNaN(ext.Tod) ? ext.Tod : _level.timeOfDay;
            float heatRaw = _diurnal.Value ? Mathf.Clamp01(Mathf.Sin(Mathf.PI * (_tod - 8f) / 12f)) * (_tod > 8 && _tod < 20 ? 1 : 0) : 0.7f;
            _H = 0.35f + 0.6f * _cond + _humBias.Value + (float.IsNaN(ext.Hum) ? 0 : ext.Hum);
            if (gameCov >= 0 && _condOv.Value < 0 && float.IsNaN(ext.Conditions)) _H += (gameCov - 0.5f) * 0.1f;   // the game's weather set pulls it a little
            // weather changes slowly (game weather set, front, time of day): ChangeTime
            if (_wxInit && !snapMenu) _heat = _heatS += (heatRaw - _heatS) * (1f - Mathf.Exp(-dts / Tc)); else _heat = _heatS = heatRaw;
            _I = (0.15f + 0.75f * _cond) * (0.3f + 0.7f * _heat) + _insBias.Value + (float.IsNaN(ext.Ins) ? 0 : ext.Ins);
            if (_wxInit) { float k = 1f - Mathf.Exp(-dts / Tc); _Hs += (_H - _Hs) * k; _Is += (_I - _Is) * k; }
            else { _Hs = _H; _Is = _I; _wxInit = true; }
            if (snapMenu) { _Hs = _H; _Is = _I; }   // in the menu, tuning applies immediately
            _H = _Hs; _I = _Is;
            _ci = Mathf.Lerp(0.15f, 0.6f, _cond) * _cirrusAmt.Value;
            int fm = ext.Front >= 0 ? ext.Front : _frontMode.Value;
            // automatic front with hysteresis; a type change fades out and in slowly
            int ft = fm;
            if (fm == 3)
            {
                ft = _front;
                if (_cond >= 0.63f) ft = 1; else if (_cond >= 0.43f && _cond <= 0.57f) ft = 2; else if (_cond <= 0.37f) ft = 0;
            }
            if (snapMenu || !_frontInit) { _front = ft; _frontAmt = ft == 0 ? 0 : 1; _frontInit = true; }
            else if (ft != _front) { _frontAmt -= dts / (Tc * 1.5f); if (_frontAmt <= 0) { _frontAmt = 0; _front = ft; } }
            else if (_front != 0) _frontAmt = Mathf.Min(1, _frontAmt + dts / (Tc * 3f));
            _effCoverage = Mathf.Clamp01((_H - 0.3f) / 0.5f) * 0.85f * _coverage.Value;
            // coverage scale from other mods: smoothed like the rest of the weather
            float covT = float.IsNaN(ext.Cov) ? 1f : ext.Cov;
            if (snapMenu || !_covInit) { _covS = covT; _covInit = true; } else _covS += (covT - _covS) * (1f - Mathf.Exp(-dts / Tc));
            _effCoverage = Mathf.Clamp01(_effCoverage * _covS);
            _effTop = _effBottom + 250 + Mathf.Pow(Mathf.Clamp01(_I), 1.6f) * 4500;
        }

        // ---------------- integration API helpers ----------------
        bool _onPrev, _extSrc, _covInit; float _covS = 1f;
        readonly Dictionary<ConfigEntryBase, float> _lastOn = new Dictionary<ConfigEntryBase, float>();

        internal void SetEntry<T>(ConfigEntry<T> e, T v, bool persist)
        {
            if (persist) { e.Value = v; return; }
            bool s = Config.SaveOnConfigSet; Config.SaveOnConfigSet = false;
            try { e.Value = v; } finally { Config.SaveOnConfigSet = s; }
        }

        ConfigEntryBase FeatureEntry(string n)
        {
            switch (n.ToLowerInvariant())
            {
                case "clouds": return _on;
                case "shadows": return _shOn;
                case "rainshafts": return _atShafts;
                case "nearrain": return _atNearRain;
                case "lightning": return _atLightning;
                case "turbulence": return _atTurb;
                case "godrays": return _atRays;
                case "lightshafts": return _atShafts2;
                case "fog": return _atFog;
                case "wetglass": return _wgOn;
                case "nuclearclouds": return Nukes?.On;
                case "keepsun": return _keepSun;
                case "rainbow": return _atRainbow;
                case "glory": return _atGlory;
                case "valleyfog": return Oro?.ValleyEntry;
                case "mountaincloud": return Oro?.CapEntry;
            }
            return null;
        }

        internal bool SetFeature(string name, bool on, bool persist)
        {
            var e = FeatureEntry(name);
            if (e == null) return false;
            if (e is ConfigEntry<bool> b) SetEntry(b, on, persist);
            else if (e is ConfigEntry<int> i) SetEntry(i, on ? 0 : 2, persist);   // NearRain: 0 auto, 2 never
            else if (e is ConfigEntry<float> f)
            {
                if (!on) { if (f.Value > 0.001f) _lastOn[f] = f.Value; SetEntry(f, 0f, persist); }
                else if (f.Value <= 0.001f) SetEntry(f, _lastOn.TryGetValue(f, out var v) ? v : (float)f.DefaultValue, persist);
            }
            return true;
        }

        internal bool GetFeature(string name)
        {
            var e = FeatureEntry(name);
            if (e is ConfigEntry<bool> b) return b.Value;
            if (e is ConfigEntry<int> i) return i.Value != 2;
            if (e is ConfigEntry<float> f) return f.Value > 0.001f;
            return false;
        }

        internal string WeatherSourceName()
        {
            if (_condOv.Value >= 0) return "menu";
            if (Sync != null && Sync.HostLive && Sync.HostInputValid && Sync.HostInput.Any) return "host";
            if (NoAtmoFXApi.In.Any) return "api";
            if (NoAtmoFXApi.Adapter.Any) return "boscali";
            return "mission";
        }

        void SaveConfig() { if (Sync != null) Sync.BeforeSave(Config.Save); else Config.Save(); }

        void SetGameCloudsHidden(bool hide)
        {
            if (_cloudLayer == null) return;
            if (hide && _hidden.Count == 0)
            {
                foreach (var r in _cloudLayer.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || !r.enabled) continue;
                    if (r.GetComponentInParent<Lightning>() != null) continue; // keep lightning
                    r.enabled = false; _hidden.Add(r);
                }
                Log.LogInfo($"Game clouds hidden ({_hidden.Count} renderers).");
            }
            else if (!hide && _hidden.Count > 0)
            {
                foreach (var r in _hidden) if (r != null) r.enabled = true;
                _hidden.Clear();
            }
        }

        static double Mod(double a, double p) { double m = a % p; return m < 0 ? m + p : m; }
        Vector3 Wrap(Vector3 g, double windMul, double per)
            => new Vector3((float)Mod(g.x - _windX * windMul, per), (float)Mod(g.y, per), (float)Mod(g.z - _windZ * windMul, per));


        static RenderTexture MakeRT(RenderTexture rt, int w, int h, RenderTextureFormat f, string n)
        {
            if (rt != null && rt.width == w && rt.height == h) return rt;
            if (rt != null) rt.Release();
            rt = new RenderTexture(w, h, 0, f) { name = n, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            rt.Create();
            return rt;
        }

        // weather maps: once per frame, by the first pass that needs them
        Vector2 _mapC; float _mapWtx; bool _mapValid, _mapFull, _reflOwnMaps; int _mapRR;
        internal void EnsureWeather(CommandBuffer cmd, Material mat, bool refl = false)
        {
            if (refl && !_reflOwnMaps && _mapValid && WxA != null) return;    // the probe reuses the camera's maps
            if (!refl && WxFrame == Time.frameCount && WxA != null) return;
            bool fresh = WxA == null || WxA.width != 1024;
            WxA = MakeRT(WxA, 1024, 1024, RenderTextureFormat.ARGBHalf, "NOC_WxA");
            WxB = MakeRT(WxB, 1024, 1024, RenderTextureFormat.ARGBHalf, "NOC_WxB");
            WxC = MakeRT(WxC, 1024, 1024, RenderTextureFormat.ARGBHalf, "NOC_WxC");
            WxP = MakeRT(WxP, 512, 512, RenderTextureFormat.RGHalf, "NOC_WxP");
            bool all = refl || fresh || _mapFull || !_mapSlicing.Value;
            // the weather itself changes over minutes (Weather > ChangeTime), so between recenterings one map per
            // frame is plenty; all four at once whenever the center moves (they must share it)
            int k = _mapRR++ & 3;
            using (new ProfilingScope(cmd, refl ? null : Perf.Weather))
            {
                if (all || k == 0) cmd.Blit((Texture)null, WxA, mat, 2);
                if (all || k == 1) cmd.Blit((Texture)null, WxP, mat, 3);
                if (all || k == 2) cmd.Blit((Texture)null, WxB, mat, 4);
                if (all || k == 3) cmd.Blit((Texture)null, WxC, mat, 5);
            }
            cmd.SetGlobalTexture(ID._WxA, WxA); cmd.SetGlobalTexture(ID._WxB, WxB); cmd.SetGlobalTexture(ID._WxC, WxC); cmd.SetGlobalTexture(ID._WarpTex, WxP);
            if (refl) { _mapValid = false; return; }   // maps now centered on the probe: the camera rebuilds its own next
            _mapFull = false;
            WxFrame = Time.frameCount;
        }

        static readonly Matrix4x4 s_DirLightProj = Matrix4x4.Ortho(-0.5f, 0.5f, -0.5f, 0.5f, -0.5f, 0.5f);

        // shadow: map on the plane perpendicular to the sun, centered on the camera (snapped to the grid so it does not shimmer)
        // one shadow map frame: plane perpendicular to the sun around a snapped center, kept in GLOBAL coordinates
        // so a map drawn on an earlier frame still lines up after the camera (or the floating origin) moved
        internal struct ShFrame { public bool ok; public Vector3 centerG; public Quaternion q; public float S; public int res; }
        internal ShFrame ShCookie, ShDepth;
        internal bool PrepareShadow(Camera cam, Material m, out ShFrame f)
        {
            f = default; f.res = Mathf.Clamp(_shRes.Value, 128, 2048);
            Light sun = _level != null ? _level.sun : null;
            if (sun == null || !sun.isActiveAndEnabled || sun.intensity <= 0 || sun.transform.forward.y > -0.02f) return false;
            Quaternion q = sun.transform.rotation;
            Vector3 right = q * Vector3.right, up = q * Vector3.up;
            float S = _shDist.Value * 2f, texel = S / f.res;
            Vector3 cp = cam.transform.position;
            float lx = Vector3.Dot(cp, right), ly = Vector3.Dot(cp, up);
            float sx = Mathf.Round(lx / texel) * texel, sy = Mathf.Round(ly / texel) * texel;
            Vector3 center = cp + right * (sx - lx) + up * (sy - ly);   // snapped center (Unity world)
            f.ok = true; f.centerG = center - Datum.originPosition; f.q = q; f.S = S;
            m.SetFloat(ID._ShSteps, Mathf.Clamp(_shSteps.Value, 4, 48));
            m.SetFloat(ID._ShOpacity, _shOpacity.Value);
            m.SetFloat(ID._ShBelow, _shBelow.Value);
            m.SetFloat(ID._ShProt, _shProt.Value);
            return true;
        }
        // plane uniforms for frame f, relative to the camera now (globals through the command buffer, so each blit
        // sees the values of its own frame)
        internal static void ApplyShFrame(CommandBuffer cmd, Camera cam, ShFrame f)
        {
            Vector3 right = f.q * Vector3.right, up = f.q * Vector3.up;
            Vector3 center = f.centerG + Datum.originPosition;
            Vector3 c0 = center - right * (f.S * 0.5f) - up * (f.S * 0.5f);
            cmd.SetGlobalVector(ID._ShO, c0 - cam.transform.position);   // camera-relative, like the rest of the shader
            cmd.SetGlobalVector(ID._ShR, right * f.S);
            cmd.SetGlobalVector(ID._ShU, up * f.S);
        }
        internal static Matrix4x4 CookieMatrix(ShFrame f)
        {
            Matrix4x4 ls = Matrix4x4.TRS(f.centerG + Datum.originPosition, f.q, Vector3.one);
            return s_DirLightProj * Matrix4x4.Scale(new Vector3(1f / f.S, 1f / f.S, 1f)) * ls.inverse;
        }
        internal int ShEvery => Mathf.Clamp(_shEvery.Value, 1, 4);

        Vector2 WindDirL()
        {
            if (_windDirSet) return _windDirL;
            var ext = NoAtmoFXApi.Effective;
            Vector2 wd = ext.HasWind ? new Vector2(ext.WindX, ext.WindZ) : _level != null && _gameWeather.Value ? new Vector2(_level.windVelocity.x, _level.windVelocity.z) : Vector2.zero;
            if (wd.sqrMagnitude < 0.01f) wd = new Vector2(0.94f, 0.34f);
            _windDirL = wd.normalized; _windDirSet = _level != null;
            return _windDirL;
        }

        // ---------- quality presets ----------
        // A preset only writes the entries below. Changing one of them by hand (F11, ConfigurationManager, another
        // mod's panel) makes the preset read Custom again; the player's own values are never overwritten unless a
        // named preset is chosen. On load, Custom is replaced by the name of the preset the values match.
        internal static readonly string[] PresetNames = { "Custom", "Low", "Medium", "High", "Ultra" };
        //                                    Downscale Temporal Steps LightSteps MaxStep MaxDistance ShadowRes ShadowSteps
        static readonly float[][] PresetVals =
        {
            new float[] { 3, 2, 128, 2, 500, 60000, 256, 8 },     // Low
            new float[] { 2, 2, 192, 3, 300, 80000, 384, 12 },    // Medium
            new float[] { 2, 2, 256, 4, 300, 80000, 512, 16 },    // High: Ultra's look, a quarter of the rays per frame
            new float[] { 2, 0, 256, 4, 300, 80000, 512, 16 },    // Ultra: everything every frame (the look up to 1.3)
        };
        ConfigEntryBase[] _pEntries;
        bool _pApplying;

        void InitPresets()
        {
            _pEntries = new ConfigEntryBase[] { _downscale, _temporal, _steps, _lightSteps, _maxStep, _maxDist, _shRes, _shSteps };
            Config.SettingChanged += (s, a) =>
            {
                if (_pApplying) return;
                var e = a.ChangedSetting;
                if (e == _preset) { if (_preset.Value == "Custom") SyncPresetName(); else ApplyPreset(_preset.Value, Config.SaveOnConfigSet); }
                else if (System.Array.IndexOf(_pEntries, e) >= 0) SyncPresetName();
            };
            if (_preset.Value == "Custom" || System.Array.IndexOf(PresetNames, _preset.Value) < 0) SyncPresetName();
            else if (MatchPreset() != _preset.Value) ApplyPreset(_preset.Value, true);   // chosen in the file (by hand or by another mod's menu)
        }

        string MatchPreset()
        {
            for (int i = 0; i < PresetVals.Length; i++)
            {
                bool ok = true;
                for (int k = 0; k < _pEntries.Length && ok; k++) ok = Mathf.Abs(System.Convert.ToSingle(_pEntries[k].BoxedValue) - PresetVals[i][k]) < 0.01f;
                if (ok) return PresetNames[i + 1];
            }
            return "Custom";
        }

        void SyncPresetName()
        {
            string n = MatchPreset();
            if (n == _preset.Value) return;
            _pApplying = true;
            try { SetEntry(_preset, n, Config.SaveOnConfigSet); } finally { _pApplying = false; }
            NoAtmoFXApi.RaisePreset(n);
        }

        internal bool ApplyPreset(string name, bool persist)
        {
            int i = System.Array.FindIndex(PresetNames, n => string.Equals(n, name, System.StringComparison.OrdinalIgnoreCase)) - 1;
            if (i < 0) return false;
            _pApplying = true;
            bool save = Config.SaveOnConfigSet; Config.SaveOnConfigSet = false;
            try
            {
                var v = PresetVals[i];
                for (int k = 0; k < _pEntries.Length; k++)
                {
                    var e = _pEntries[k];
                    if (e is ConfigEntry<int> ei) ei.Value = Mathf.RoundToInt(v[k]); else if (e is ConfigEntry<float> ef) ef.Value = v[k];
                }
                _preset.Value = PresetNames[i + 1];
            }
            finally { Config.SaveOnConfigSet = save; _pApplying = false; }
            if (persist) Config.Save();
            NoAtmoFXApi.RaisePreset(_preset.Value);
            return true;
        }

        // ---------- temporal reprojection state (main camera) ----------
        internal RenderTexture TFresh, THist0, THist1;
        internal int THistIdx, TLastFrame = -10, TPhase; internal bool THistOk; internal Camera TCam;
        internal Vector3 TNowBL, TNowRx, TNowUy, TNowF, TNowPos, TPrevBL, TPrevRx, TPrevUy, TPrevF, TPrevPos;
        internal float TFlashPrev, TNukePrev;
        internal int TemporalMode => Mathf.Clamp(_temporal.Value, 0, 2);

        // ---------- uniforms (called by the pass) ----------
        internal Material Prepare(Camera cam, bool hdr, out int downscale)
        {
            downscale = Mathf.Clamp(_downscale.Value, 1, 4);
            DownscaleNow = downscale;
            var m = _mat;
            Transform tr = cam.transform;
            cam.CalculateFrustumCorners(new Rect(0, 0, 1, 1), 1f, Camera.MonoOrStereoscopicEye.Mono, _corners);
            m.SetVector(ID._CornerBL, tr.TransformVector(_corners[0]));
            m.SetVector(ID._CornerTL, tr.TransformVector(_corners[1]));
            m.SetVector(ID._CornerTR, tr.TransformVector(_corners[2]));
            m.SetVector(ID._CornerBR, tr.TransformVector(_corners[3]));
            m.SetVector(ID._CamFwd, tr.forward);
            TNowBL = tr.TransformVector(_corners[0]); TNowRx = tr.TransformVector(_corners[3]) - TNowBL; TNowUy = tr.TransformVector(_corners[1]) - TNowBL; TNowF = tr.forward;

            Vector3 g = tr.position - Datum.originPosition; // global position (y above sea level)
            float Rp = _planetR.Value;
            m.SetVector(ID._PlanetC, new Vector3(0, -(g.y + Rp), 0));
            m.SetFloat(ID._R, Rp);
            
            float perC = _perC.Value, perCell = _perCell.Value, perS = _perS.Value, perD = _perD.Value;
            double ev = _evolveT;
            // +5e5: the Mod seam sits 500 km away (it used to be at the map center and the whole field jumped when crossing it)
            var G2 = new Vector2((float)Mod(g.x - _windX + 5e5, 1e6), (float)Mod(g.z - _windZ + 5e5, 1e6));
            m.SetVector(ID._G, G2);
            TNowPos = new Vector3(G2.x, g.y, G2.y);
            float wtx = 2f * _maxDist.Value / 512f;   // coarsest map texel (distortion)
            // the weather maps stay put in cloud space (their center snapped to the texel grid) and are only
            // recentered after the camera has moved a few texels: the content at a given place is the same either
            // way, so recentering is seamless; in between they are refreshed one map per frame (MapSlicing)
            bool reflCam = cam.cameraType == CameraType.Reflection;
            Vector2 snap = new Vector2(Mathf.Round(G2.x / wtx) * wtx, Mathf.Round(G2.y / wtx) * wtx);
            if (!reflCam)
            {
                float lim = _mapSlicing.Value ? 4f * wtx : 0f;
                Vector2 d = G2 - _mapC;
                if (!_mapValid || wtx != _mapWtx || Mathf.Abs(d.x) > lim || Mathf.Abs(d.y) > lim)
                {
                    _mapC = snap; _mapWtx = wtx; _mapFull = true; _mapValid = true;
                }
                _reflOwnMaps = false;
                GDLast = G2 - _mapC;
            }
            else
            {
                // reflection probe: reuse the camera's maps when the probe sits inside them (it used to rebuild all
                // of them for each of the 6 faces and again for the camera afterwards: a spike every 5 s)
                Vector2 d = G2 - _mapC;
                _reflOwnMaps = !_mapSlicing.Value || !_mapValid || wtx != _mapWtx || Mathf.Abs(d.x) > _maxDist.Value * 0.25f || Mathf.Abs(d.y) > _maxDist.Value * 0.25f;
                GDLast = _reflOwnMaps ? G2 - snap : d;
            }
            m.SetVector(ID._GD, GDLast);
            // mountain fog (valley fog / upslope cloud): own layer, own height map
            if (Oro != null) { if (!reflCam) Oro.Tick(g, _tod, _cond, _H, _windS); Oro.Apply(m, g); Oro.SetWind(m, _windS); }
            // atmosphere
            m.SetFloat(ID._RainOn, _atShafts.Value && (_H > 0.55f || _I > 0.5f || _front != 0) ? 1f : 0f);
            m.SetFloat(ID._RainVis, _atRainVis.Value);
            m.SetFloat(ID._Rainbow, _atRainbow.Value); m.SetFloat(ID._Glory, _atGlory.Value);
            m.SetFloat(ID._WetDark, _wetDark.Value);
            // overcast deck: from "cloudy" to "storm" (smoothed conditions)
            m.SetFloat(ID._Deck, Mathf.Clamp01(Mathf.InverseLerp(0.55f, 0.85f, _condS)) * _deck.Value);
            m.SetVector(ID._WindVel, _level != null ? _level.windVelocity : Vector3.zero);
            Atmos?.Apply(m, cam);
            m.SetVector(ID._OffS, Wrap(g, 1.0, perS) + new Vector3(0, (float)Mod(ev, perS), 0));
            m.SetVector(ID._OffD, Wrap(g, 1.3, perD) + new Vector3(0, (float)Mod(ev * 1.7, perD), 0));
            m.SetFloat(ID._PerCl, perC); m.SetFloat(ID._PerCell, perCell); m.SetFloat(ID._PerS, perS); m.SetFloat(ID._PerD, perD);
            m.SetFloat(ID._PerHum, _perHum.Value); m.SetFloat(ID._PerIns, _perIns.Value); m.SetFloat(ID._PerWarp, _perWarp.Value);
            m.SetFloat(ID._PerTurb, _perTurb.Value); m.SetFloat(ID._PerCb, _perCb.Value);
            m.SetFloat(ID._Warp, _warp.Value); m.SetFloat(ID._Turb, _turb.Value); m.SetFloat(ID._Stretch, _stretch.Value);
            m.SetFloat(ID._SizeMix, _sizeMix.Value); m.SetFloat(ID._BaseRough, _baseRough.Value); m.SetFloat(ID._Anvil, _anvil.Value);
            m.SetFloat(ID._AnvilShift, _anvilShift.Value); m.SetFloat(ID._Veer, _veer.Value);
            m.SetFloat(ID._Hum, _H); m.SetFloat(ID._Ins, _I);
            m.SetFloat(ID._BaseRef, _effBottom); m.SetFloat(ID._BaseVar, _baseVar.Value);
            m.SetFloat(ID._CoverScale, _coverage.Value);
            m.SetFloat(ID._Cirrus, _ownCirrus.Value ? _ci : 0f); m.SetFloat(ID._MidAmt, _midAmt.Value);
            m.SetFloat(ID._CiAlt, 9500f);
            m.SetFloat(ID._CiTile, _ciTile.Value);
            m.SetFloat(ID._FarSteps, Mathf.Clamp(_farSteps.Value, 0, 6));
            m.SetFloat(ID._FarShadow, _farShadow.Value);
            m.SetFloat(ID._Blur, _blur.Value); m.SetFloat(ID._ShDark, _shDark.Value); m.SetFloat(ID._FarSoft, _farSoft.Value);
            m.SetFloat(ID._ShDOn, (ShDFrame == Time.frameCount && ShDCam == cam) ? 1f : 0f);
            m.SetFloat(ID._PixAng, 2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * downscale / Mathf.Max(1, cam.pixelHeight));
            m.SetFloat(ID._FrontType, _front);
            m.SetFloat(ID._FrontAmt, _frontAmt);
            m.SetFloat(ID._FrontPos, Vector2.Dot(new Vector2(5e5f, 5e5f), WindDirL()) - _frontDist.Value * 1000f);
            m.SetFloat(ID._FrontWidth, _frontWidth.Value * 1000f);
            float cbTop = Mathf.Max(_cbTop.Value, 9300f);
            m.SetFloat(ID._CbTop, cbTop);
            m.SetFloat(ID._Bottom, 200f);
            m.SetFloat(ID._VolTop, cbTop * (_supercell.Value > 0 ? 1.12f : 1f));
            // nuclear blast: rings/caps can be higher than the normal cloud volume
            if (Nukes != null) { Nukes.Apply(m, cam, g, _windX, _windZ, _H); if (Nukes.Active && Nukes.TopAlt > 0) m.SetFloat(ID._VolTop, Mathf.Max(cbTop * (_supercell.Value > 0 ? 1.12f : 1f), Nukes.TopAlt)); }
            m.SetFloat(ID._Supercell, _supercell.Value);
            m.SetFloat(ID._WExt, _maxDist.Value);
            m.SetVector(ID._WTS, new Vector4(1f / 1024, 1f / 1024, 1024, 1024));
            m.SetVector(ID._PTS, new Vector4(1f / 512, 1f / 512, 512, 512));

            m.SetFloat(ID._Density, _density.Value);
            m.SetFloat(ID._Erosion, _erosion.Value);
            m.SetFloat(ID._Dome, _dome.Value);
            m.SetFloat(ID._Billow, _billow.Value);
            m.SetFloat(ID._Soft, _soft.Value);
            m.SetFloat(ID._Shear, _shear.Value);
            // direction of cloud streets, fronts and anvils: fixed per mission. The game picks a new wind heading
            // every so often; rotating this direction rotates the whole field around a point hundreds of km away
            // and the clouds slide for kilometres. The wind still carries the clouds (drift).
            m.SetVector(ID._WindDir, WindDirL());

            m.SetFloat(ID._Steps, _steps.Value);
            m.SetFloat(ID._LightSteps, Mathf.Clamp(_lightSteps.Value, 1, 6));
            m.SetFloat(ID._MinStep, _minStep.Value);
            m.SetFloat(ID._StepGrowth, _stepGrowth.Value);
            m.SetFloat(ID._MaxStep, Mathf.Max(_maxStep.Value, _minStep.Value));
            m.SetFloat(ID._MaxDist, _maxDist.Value);
            m.SetFloat(ID._Jitter, _jitter.Value ? 1 : 0);
            m.SetFloat(ID._Frame, Time.frameCount % 64);
            m.SetFloat(ID._UseDepth, _useDepth.Value ? 1 : 0);
            m.SetFloat(ID._Debug, _debug.Value ? 1 : 0);

            m.SetFloat(ID._Absorb, _absorb.Value);
            m.SetFloat(ID._LightAbsorb, _lightAbsorb.Value);
            m.SetFloat(ID._G1, _g1.Value); m.SetFloat(ID._G2, _g2.Value); m.SetFloat(ID._GMix, _gMix.Value);
            m.SetFloat(ID._Powder, _powder.Value); m.SetFloat(ID._PowderK, _powderK.Value);
            m.SetFloat(ID._MsA, _msA.Value); m.SetFloat(ID._MsB, _msB.Value);
            m.SetFloat(ID._Exposure, _exposure.Value);
            m.SetFloat(ID._Tonemap, _tonemap.Value == 1 || (_tonemap.Value == 2 && !hdr) ? 1 : 0);

            // sun (or moon at night)
            Light sun = _level.sun, moon = _level.moon;
            // sun until 6° below the horizon (the shader already applies the planet shadow, so only high clouds catch the
            // sunset); then the moon, if it is above the horizon. Neither: sky light only.
            float sunEl = sun != null ? -sun.transform.forward.y : -1f;
            // the game DISABLES the sun object when the camera is below the layer in bad weather; color and direction
            // keep updating, so the clouds use the sun anyway (the shadow comes from the raymarch itself)
            bool useSun = sun != null && sun.enabled && (sun.color.r + sun.color.g + sun.color.b) > 0 && sun.intensity > 0 && sunEl > -0.105f;
            bool moonOk = moon != null && moon.isActiveAndEnabled && -moon.transform.forward.y > -0.05f;
            Light key = useSun ? sun : (moonOk ? moon : null);
            Vector3 sunDir = key != null ? -key.transform.forward : Vector3.up;
            Color sunCol = key != null ? Color.Lerp(key.color, Color.white, _sunWhite.Value) * key.intensity : Color.black;
            if (key == moon && key != null) sunCol = Color.Lerp(moon.color, new Color(0.75f, 0.82f, 1f), 0.5f) * Mathf.Max(moon.intensity, 0.05f) * _atMoon.Value;
            // volumetric light shafts: only with this frame's cloud shadow and a partly covered sky
            ShaftsActive = false;
            if (useSun && sunEl > -0.02f && _atShafts2.Value > 0.001f && ShDFrame == Time.frameCount && ShDCam == cam)
            {
                float covF = Mathf.Clamp01(_effCoverage * 3f);
                float amt = _atShafts2.Value * covF * Mathf.Clamp01(sunEl * 8f + 0.3f);
                if (amt > 0.001f)
                {
                    ShaftsActive = true;
                    m.SetFloat(ID._ShaftAmt, amt);
                    m.SetFloat(ID._ShaftSig, 6e-5f + RenderSettings.fogDensity * 0.5f);
                }
            }
            // god rays: sun above the horizon and near/inside the screen
            RaysActive = false;
            if (useSun && sunEl > -0.02f && _atRays.Value > 0.001f)
            {
                Vector3 vp = cam.WorldToViewportPoint(cam.transform.position + sunDir * 10000f);
                if (vp.z > 0 && vp.x > -0.6f && vp.x < 1.6f && vp.y > -0.6f && vp.y < 1.6f)
                {
                    RaysActive = true;
                    m.SetVector(ID._SunUV, new Vector2(vp.x, vp.y));
                    float edge = Mathf.Clamp01(1.6f - Mathf.Max(Mathf.Abs(vp.x - 0.5f), Mathf.Abs(vp.y - 0.5f)) * 2f / 1.1f);
                    m.SetFloat(ID._RaysAmt, _atRays.Value * Mathf.Clamp01(sunEl * 8f + 0.3f) * edge);
                }
            }
            // storm: the upper sky (anvils, altostratus) removes much of the direct sun from ALL clouds
            // and the light turns diffuse and grey (no bright white tops, no blue in the bases)
            float storm = Mathf.InverseLerp(0.55f, 0.9f, _condS) * _stormDim.Value;
            storm = Mathf.Clamp01(storm);
            Color Gray(Color c) { float g = c.r * 0.3f + c.g * 0.55f + c.b * 0.15f; return new Color(g, g, g * 1.03f, c.a); }
            sunCol = Color.Lerp(sunCol, Gray(sunCol), 0.5f * storm) * (1f - 0.3f * storm);
            m.SetVector(ID._SunDir, sunDir);
            m.SetVector(ID._SunCol, sunCol * _sunMul.Value);
            Color amb = RenderSettings.ambientSkyColor * RenderSettings.ambientIntensity;
            Color ambB = RenderSettings.ambientEquatorColor * RenderSettings.ambientIntensity;
            // below the game's layer in bad weather the game makes sky light grey and 25% dimmer (fake overcast):
            // on the clouds use the sky color as above the layer (same math as the game, without the grey part)
            if (_level.conditions > 0.6f)
            {
                var ga = GameAssets.i;
                if (ga != null && ga.skyColor != null)
                {
                    Color sky = ga.skyColor.Evaluate(_level.timeOfDay / 24f) * 1.3f;
                    var mn = _level.moon;
                    if (mn != null) sky += mn.color * 1.5f * mn.intensity * Mathf.Clamp01((0.05f - RenderSettings.ambientIntensity + 0.5f * mn.intensity) * 40f);
                    amb = sky * RenderSettings.ambientIntensity;
                }
            }
            amb = Color.Lerp(amb, Gray(amb), 0.75f * storm) * (1f - 0.25f * storm);
            ambB = Color.Lerp(ambB, Gray(ambB), 0.75f * storm) * (1f - 0.25f * storm);
            m.SetVector(ID._AmbTop, amb * _ambMul.Value);
            m.SetVector(ID._AmbBottom, ambB * _ambMul.Value * 0.7f);

            m.SetFloat(ID._FogMode, RenderSettings.fog ? (RenderSettings.fogMode == FogMode.Linear ? 1 : RenderSettings.fogMode == FogMode.Exponential ? 2 : 3) : 0);
            m.SetFloat(ID._FogStart, RenderSettings.fogStartDistance);
            m.SetFloat(ID._FogEnd, RenderSettings.fogEndDistance);
            // in bad weather (below the game's layer) the game adds +0.001 fog to fake an overcast sky:
            // on our clouds this killed all the light (5 km of cloud turned into grey fog). Remove that part.
            float fogD = RenderSettings.fogDensity;
            if (_fogWritten < 0 && _cloudLayer != null && _level.conditions > 0.6f)
            {
                float n3 = Mathf.Clamp01((_cloudLayer.transform.position.y + 200f - cam.transform.position.y) * 0.005f);
                float n4 = n3 * Mathf.InverseLerp(0.8f, 1f, _level.conditions);
                fogD = Mathf.Max(fogD - n4 * 0.001f, 3e-5f);
            }
            m.SetFloat(ID._FogDensity, fogD);
            // rain fog (pass 14): height layer below the base
            m.SetFloat(ID._RFogSig, FogExtra);
            m.SetFloat(ID._RFogH, Mathf.Clamp(_effBottom * 0.6f, 300f, 1500f));
            m.SetFloat(ID._RFogTop, _effBottom + 100f);
            float tauUp = (Wet != null && Wet.ProbeValid) ? Wet.ProbeTau : 0f;
            m.SetFloat(ID._RFogSunVis, Mathf.Exp(-tauUp));
            m.SetColor(ID._FogCol, RenderSettings.fogColor);
            m.SetFloat(ID._FogAmt, _fogAmt.Value);
            return m;
        }

        // ---------- menu ----------
        void OnGUI()
        {
            if (!_menu) return;
            _win = GUI.Window(46369411, _win, DrawWin, T("NoAtmoFX: live tuning (F11 closes and saves)", "NoAtmoFX: ajuste ao vivo (F11 fecha e salva)"));
        }

        void DrawWin(int id)
        {
            foreach (var row in new[] { new[] { "General", "Weather", "Layer", "Shape", "Scale", "Variation" }, new[] { "Wind", "Storm", "Lighting", "Shadows", "Glass", "Atmosphere", "Nuclear", "Quality", "Multiplayer" } })
            {
                GUILayout.BeginHorizontal();
                foreach (var s in row)
                    if (GUILayout.Toggle(_section == s, Pt && PtTab.TryGetValue(s, out var ps) ? ps : s, GUI.skin.button)) _section = s;
                GUILayout.EndHorizontal();
            }
            if (Pt) GUILayout.Label($"Tempo: condições {_cond:F2}, hora {_tod:F1} (aquecimento {_heat:F2}), umidade {_H:F2}, instabilidade {_I:F2}, frente {(_front == 1 ? "fria" : _front == 2 ? "quente" : "nenhuma")}, base ref {_effBottom:F0} m, cobertura {_effCoverage:F2}, vento {(_level != null ? _level.windVelocity.magnitude : 0):F1} m/s, fonte {WeatherSourceName()}");
            else GUILayout.Label($"Weather: conditions {_cond:F2}, hour {_tod:F1} (heating {_heat:F2}), humidity {_H:F2}, instability {_I:F2}, front {(_front == 1 ? "cold" : _front == 2 ? "warm" : "none")}, base ref {_effBottom:F0} m, coverage {_effCoverage:F2}, wind {(_level != null ? _level.windVelocity.magnitude : 0):F1} m/s, source {WeatherSourceName()}");
            if (Boscali != null && Boscali.Present) GUILayout.Label((Pt ? "Clima Boscali (Unified Effects): " : "Boscali weather (Unified Effects): ") + Boscali.Status);
            if (_section == "Multiplayer" && Sync != null)
            {
                string st = !Sync.Shared ? T("not in a multiplayer mission", "fora de missão multijogador") : Sync.Status;
                if (Pt && Sync.Shared) st = Sync.IsHost ? st.Replace("client(s) with NoAtmoFX", "cliente(s) com NoAtmoFX") : Sync.HostHasMod ? "seguindo o host (NoAtmoFX)" : "relógio da missão (host sem NoAtmoFX ou ainda não respondeu)";
                GUILayout.Label(T("Sync: ", "Sincronia: ") + st + (Sync.Shared ? $", {T("mission time", "tempo da missão")} {Sync.T:F0} s" : ""));
                GUILayout.Label(T("Cloud settings code: ", "Código das configurações de nuvem: ") + Sync.SettingsCode() + (Sync.UsingHostSettings ? T(" (host's)", " (do host)") : "") +
                    T("  — same code = same clouds when the host has no NoAtmoFX.", "  — mesmo código = mesmas nuvens quando o host não tem NoAtmoFX."));
            }
            if (_section == "Quality") { if (_perfMeter.Value) Perf.Draw(T); DrawBench(); }
            if (_section == "Nuclear" && Nukes != null && GUILayout.Button(T("Test blast now (clouds only, ahead of the camera)", "Explosão de teste agora (só nas nuvens, à frente da câmera)"))) Nukes.TestNow = true;
            _scroll = GUILayout.BeginScrollView(_scroll);
            var small = new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = true };
            small.normal.textColor = new Color(0.75f, 0.75f, 0.75f);
            foreach (var kv in Config)
            {
                if (kv.Key.Section != _section) continue;
                var e = kv.Value; string key = kv.Key.Key;
                if (e is ConfigEntry<float> f)
                {
                    var r = e.Description.AcceptableValues as AcceptableValueRange<float>;
                    GUILayout.Label($"{key}: {f.Value:G4}");
                    float lo = r?.MinValue ?? 0, hi = r?.MaxValue ?? Mathf.Max(1, f.Value * 4);
                    float v = GUILayout.HorizontalSlider(f.Value, lo, hi);
                    if (Mathf.Abs(v - f.Value) > (hi - lo) * 0.0001f) f.Value = v;
                }
                else if (e is ConfigEntry<int> i)
                {
                    var r = e.Description.AcceptableValues as AcceptableValueRange<int>;
                    GUILayout.Label($"{key}: {i.Value}");
                    int v = Mathf.RoundToInt(GUILayout.HorizontalSlider(i.Value, r?.MinValue ?? 0, r?.MaxValue ?? 100));
                    if (v != i.Value) i.Value = v;
                }
                else if (e is ConfigEntry<bool> b) b.Value = GUILayout.Toggle(b.Value, " " + key);
                else if (e is ConfigEntry<string> sv && e.Description.AcceptableValues is AcceptableValueList<string> lv)
                {
                    GUILayout.Label($"{key}: {sv.Value}");
                    GUILayout.BeginHorizontal();
                    foreach (var o in lv.AcceptableValues)
                        if (GUILayout.Toggle(sv.Value == o, o, GUI.skin.button) && sv.Value != o) sv.Value = o;
                    GUILayout.EndHorizontal();
                }
                else continue;
                string desc = e.Description.Description;
                if (Pt && PtDesc.TryGetValue(kv.Key.Section + "/" + key, out var pd)) desc = pd;
                if (!string.IsNullOrEmpty(desc)) GUILayout.Label(desc, small);
                GUILayout.Space(4);
            }
            GUILayout.EndScrollView();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(T("Reset this tab", "Padrão desta aba")))
                foreach (var kv in Config)
                    if (kv.Key.Section == _section && !(kv.Value is ConfigEntry<KeyboardShortcut>)) kv.Value.BoxedValue = kv.Value.DefaultValue;
            if (GUILayout.Button(T("Save", "Salvar"))) SaveConfig();
            if (GUILayout.Button(T("Diagnostic to log", "Diagnóstico no log"))) Dump();
            GUILayout.EndHorizontal();
            GUI.DragWindow(new Rect(0, 0, 10000, 22));
        }

        // ---------- benchmark (Quality tab) ----------
        // The game build gives mods no GPU timers, so the cost of each part is measured by switching parts on and
        // off for a few seconds each and comparing the frame time. Settings are restored and never saved.
        struct BenchStep { public string en, pt; public System.Action set; }
        BenchStep[] _bSteps; int _bIdx = -1; float _bT0; double _bSum; int _bN; float _bWorst; readonly List<float> _bDts = new List<float>();
        string[] _bRes; bool _bSave;
        bool _bOn, _bSh, _bRain, _bSlice; float _bRays, _bShafts; int _bEvery, _bDs, _bTmp;
        const float BenchWarm = 1.5f, BenchLen = 5f;

        void StartBench()
        {
            _bOn = _on.Value; _bSh = _shOn.Value; _bRain = _atShafts.Value; _bSlice = _mapSlicing.Value; _bRays = _atRays.Value; _bShafts = _atShafts2.Value; _bEvery = _shEvery.Value; _bDs = _downscale.Value; _bTmp = _temporal.Value;
            void Mine() { _on.Value = _bOn; _shOn.Value = _bSh; _atShafts.Value = _bRain; _mapSlicing.Value = _bSlice; _atRays.Value = _bRays; _atShafts2.Value = _bShafts; _shEvery.Value = _bEvery; _downscale.Value = _bDs; _temporal.Value = _bTmp; }
            _bSteps = new[]
            {
                new BenchStep { en = "Mod off", pt = "Mod desligado", set = () => { Mine(); _on.Value = false; } },
                new BenchStep { en = "Clouds only (no shadows, rays, shafts, rain curtains)", pt = "Só nuvens (sem sombra, raios, feixes, cortinas)", set = () => { Mine(); _shOn.Value = false; _atRays.Value = 0; _atShafts2.Value = 0; _atShafts.Value = false; } },
                new BenchStep { en = "Clouds + shadows", pt = "Nuvens + sombras", set = () => { Mine(); _atRays.Value = 0; _atShafts2.Value = 0; _atShafts.Value = false; } },
                new BenchStep { en = "Your settings", pt = "Suas configurações", set = () => Mine() },
                new BenchStep { en = "Your settings, old 1.1 way", pt = "Suas configurações, jeito antigo 1.1", set = () => { Mine(); _mapSlicing.Value = false; _shEvery.Value = 1; } },
                new BenchStep { en = "Your settings, one Downscale step lower", pt = "Suas configurações, Downscale um nível abaixo", set = () => { Mine(); _downscale.Value = Mathf.Min(_bDs + 1, 4); } },
                _bTmp > 0 ? new BenchStep { en = "Your settings, temporal off", pt = "Suas configurações, temporal desligado", set = () => { Mine(); _temporal.Value = 0; } }
                          : new BenchStep { en = "Your settings, temporal 2x2", pt = "Suas configurações, temporal 2x2", set = () => { Mine(); _temporal.Value = 2; } },
            };
            _bRes = new string[_bSteps.Length];
            _bSave = Config.SaveOnConfigSet; Config.SaveOnConfigSet = false;
            _bIdx = 0; _bSteps[0].set(); _bT0 = Time.unscaledTime; _bSum = 0; _bN = 0; _bDts.Clear();
            Log.LogInfo($"Benchmark started: GPU {SystemInfo.graphicsDeviceName}, {Screen.width}x{Screen.height}, Downscale {_bDs}, Temporal {_bTmp}, Steps {_steps.Value}, preset {_preset.Value}");
        }

        void StopBench(bool done)
        {
            if (_bIdx < 0) return;
            _on.Value = _bOn; _shOn.Value = _bSh; _atShafts.Value = _bRain; _mapSlicing.Value = _bSlice; _atRays.Value = _bRays; _atShafts2.Value = _bShafts; _shEvery.Value = _bEvery; _downscale.Value = _bDs; _temporal.Value = _bTmp;
            Config.SaveOnConfigSet = _bSave;
            _bIdx = -1;
            if (done) Log.LogInfo("Benchmark results:\n" + string.Join("\n", _bRes));
        }

        void BenchTick()
        {
            if (_bIdx < 0) return;
            float t = Time.unscaledTime - _bT0;
            if (t > BenchWarm) { float dt = Time.unscaledDeltaTime * 1000f; _bSum += dt; _bN++; _bDts.Add(dt); }
            if (t < BenchWarm + BenchLen) return;
            _bDts.Sort();
            float avg = _bN > 0 ? (float)(_bSum / _bN) : 0, p99 = _bDts.Count > 0 ? _bDts[Mathf.Min(_bDts.Count - 1, (int)(_bDts.Count * 0.99f))] : 0;
            var st = _bSteps[_bIdx];
            _bRes[_bIdx] = $"{T(st.en, st.pt)}: {avg:F1} ms ({(avg > 0 ? 1000f / avg : 0):F0} fps), {T("worst 1%", "pior 1%")} {p99:F1} ms";
            if (++_bIdx >= _bSteps.Length) { StopBench(true); return; }
            _bSteps[_bIdx].set(); _bT0 = Time.unscaledTime; _bSum = 0; _bN = 0; _bDts.Clear();
        }

        void DrawBench()
        {
            if (_bIdx >= 0)
            {
                GUILayout.Label(T($"Benchmark running ({_bIdx + 1}/{_bSteps.Length}): {_bSteps[_bIdx].en}. Keep the camera still.",
                                  $"Teste rodando ({_bIdx + 1}/{_bSteps.Length}): {_bSteps[_bIdx].pt}. Deixe a câmera parada."));
                if (GUILayout.Button(T("Stop", "Parar"))) StopBench(false);
            }
            else if (GUILayout.Button(T($"Benchmark: switch parts on/off ({_bSteps?.Length ?? 6} x {BenchWarm + BenchLen:F0} s, camera still; settings are restored)",
                                        $"Teste de desempenho: liga/desliga cada parte ({_bSteps?.Length ?? 6} x {BenchWarm + BenchLen:F0} s, câmera parada; as configurações voltam)")))
                StartBench();
            if (_bRes != null)
                foreach (var r in _bRes) if (r != null) GUILayout.Label(r);
        }

        void Dump()
        {
            var sb = new StringBuilder("=== NoAtmoFX diagnostic ===\n");
            var cam = TargetCamera();
            sb.AppendLine($"GPU {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType}), pipeline {GraphicsSettings.currentRenderPipeline?.name}");
            if (cam != null)
            {
                var d = cam.GetUniversalAdditionalCameraData();
                sb.AppendLine($"camera '{cam.name}' pos {cam.transform.position} clip {cam.nearClipPlane}/{cam.farClipPlane} hdr {cam.allowHDR} msaa {cam.allowMSAA} renderer {d?.scriptableRenderer?.GetType().Name} post {d?.renderPostProcessing} stack {d?.cameraStack?.Count}");
                sb.AppendLine($"global altitude {(cam.transform.position - Datum.originPosition).y:F0} m; origin {Datum.originPosition}");
            }
            else sb.AppendLine("main camera: none");
            if (_level != null)
                sb.AppendLine($"weather: conditions {_level.conditions:F2}, cloudHeight {_level.cloudHeight:F0}, wind {_level.windVelocity}, hour {_level.timeOfDay:F1}, weatherSet {(_weatherSet as WeatherSet)?.displayName} coverage {(_weatherSet as WeatherSet)?.coverage}");
            sb.AppendLine($"clouds: on {_on.Value}, effective coverage {_effCoverage:F2}, layer {_effBottom:F0}-{_effTop:F0}, hidden {_hidden.Count}");
            sb.AppendLine($"fog: {RenderSettings.fog} {RenderSettings.fogMode} dens {RenderSettings.fogDensity} color {RenderSettings.fogColor}; ambient {RenderSettings.ambientMode} sky {RenderSettings.ambientSkyColor} int {RenderSettings.ambientIntensity}");
            if (_level != null && _level.sun != null) sb.AppendLine($"sun: dir {-_level.sun.transform.forward} color {_level.sun.color} int {_level.sun.intensity}");
            Log.LogInfo(sb.ToString());
        }
    }

    // URP pass: raymarch into a downscaled RT + composite over the camera color.
    class CloudPass : ScriptableRenderPass
    {
        readonly NOCloudsPlugin _p;
        static readonly int RT = Shader.PropertyToID("_NOCloudsRT");
        internal static readonly int WaterMode = Shader.PropertyToID("_NOCWater");
        static readonly FieldInfo F_opaque = typeof(UniversalRenderer).GetField("m_OpaqueColor", BindingFlags.Instance | BindingFlags.NonPublic);
        static bool _opqLogged;
        static readonly int WA = Shader.PropertyToID("_WxA");
        static readonly int WB = Shader.PropertyToID("_WxB");
        static readonly int WC = Shader.PropertyToID("_WxC");
        static readonly int WP = Shader.PropertyToID("_WarpTex");
        float _probeT = -10;

        System.Action<AsyncGPUReadbackRequest> _onProbe;
        void OnProbe(AsyncGPUReadbackRequest r)
        {
            if (r.hasError) return;
            var d = r.GetData<Color>();
            var v = d[0];
            var w0 = _p.Wet; var at = _p.Atmos;
            if (w0 != null) { w0.ProbeCloud = v.r; w0.ProbeTau = v.g; w0.ProbeCb = v.b; w0.ProbeAlt = v.a; w0.ProbeValid = true; }
            _p.ProbeCloudAt = v.r; _p.ProbeTauAt = v.g; _p.ProbeAtValid = true;
            if (at != null && d.Length > 1) { at.ProbeRain = d[1].r; at.ProbeCb = d[1].g; at.ProbeRainValid = true; }
        }

        // ---------- temporal reprojection ----------
        static readonly int T_Fresh = Shader.PropertyToID("_TFresh"), T_Hist = Shader.PropertyToID("_THist"), T_Size = Shader.PropertyToID("_TSize"),
            T_Mode = Shader.PropertyToID("_TMode"), T_BL = Shader.PropertyToID("_TPrevBL"), T_Rx = Shader.PropertyToID("_TPrevRx"),
            T_Uy = Shader.PropertyToID("_TPrevUy"), T_F = Shader.PropertyToID("_TPrevF"), T_Move = Shader.PropertyToID("_TMove");
        static int _tSupport = -1;
        static bool TemporalSupported(Material mat)
        {
            if (_tSupport < 0)
            {
                bool fmt = SystemInfo.IsFormatSupported(GraphicsFormat.R32G32B32A32_UInt, FormatUsage.Render);
                _tSupport = fmt && mat.passCount >= 18 ? 1 : 0;
                NOCloudsPlugin.Log.LogInfo(_tSupport == 1 ? "Temporal reprojection available" :
                    "Temporal reprojection unavailable (" + (!fmt ? "no integer render target" : "shader bundle older than 1.3, run NOClouds > Build bundle") + "); clouds are drawn in full");
            }
            return _tSupport == 1;
        }

        static RenderTexture MakeRT(RenderTexture rt, int w, int h, GraphicsFormat f, FilterMode fm, string name)
        {
            if (rt != null && rt.width == w && rt.height == h) return rt;
            if (rt != null) rt.Release();
            rt = new RenderTexture(w, h, f, GraphicsFormat.None) { name = name, filterMode = fm, wrapMode = TextureWrapMode.Clamp };
            rt.Create();
            return rt;
        }

        static float Wrap(float d) => d > 5e5f ? d - 1e6f : d < -5e5f ? d + 1e6f : d;

        // Marches only part of the pixels (1 of 2x2, or 1 of 2) and rebuilds the full image from the previous one.
        // A full march (into the history, so the next frames have a clean start) when there is nothing to reproject
        // from or the light changes too fast for old pixels: lightning, nuclear flash, camera cut, size change.
        RenderTexture MarchTemporal(CommandBuffer cmd, Camera cam, Material mat, int w, int h)
        {
            var p = _p;
            int mode = p.TemporalMode;
            int sx = 2, sy = mode == 1 ? 1 : 2;
            int fw = (w + sx - 1) / sx, fh = (h + sy - 1) / sy;
            p.THist0 = MakeRT(p.THist0, w, h, GraphicsFormat.R16G16B16A16_SFloat, FilterMode.Bilinear, "NOClouds history A");
            p.THist1 = MakeRT(p.THist1, w, h, GraphicsFormat.R16G16B16A16_SFloat, FilterMode.Bilinear, "NOClouds history B");
            bool sizeChanged = p.TFresh == null || p.TFresh.width != fw || p.TFresh.height != fh;
            p.TFresh = MakeRT(p.TFresh, fw, fh, GraphicsFormat.R32G32B32A32_UInt, FilterMode.Point, "NOClouds temporal");
            var prev = p.THistIdx == 0 ? p.THist0 : p.THist1;
            var cur = p.THistIdx == 0 ? p.THist1 : p.THist0;

            float flash = p.Atmos != null ? p.Atmos.FlashNow : 0, nk = p.Nukes != null ? p.Nukes.LightNow : 0;
            Vector3 move = p.TNowPos - p.TPrevPos; move.x = Wrap(move.x); move.z = Wrap(move.z);
            bool full = !p.THistOk || sizeChanged || p.TCam != cam || Time.frameCount - p.TLastFrame != 1
                     || flash > 0.01f || p.TFlashPrev > 0.01f
                     || Mathf.Abs(nk - p.TNukePrev) > 0.03f * Mathf.Max(nk, 0.05f)
                     || Vector3.Angle(p.TNowF, p.TPrevF) > 10f || move.magnitude > 500f;
            p.TFlashPrev = flash; p.TNukePrev = nk;

            using (new ProfilingScope(cmd, Perf.March))
            {
                if (full) cmd.Blit((Texture)null, cur, mat, 0);
                else
                {
                    p.TPhase = (p.TPhase + 1) & (mode == 1 ? 1 : 3);
                    cmd.SetGlobalVector(T_Size, new Vector4(w, h, fw, fh));
                    cmd.SetGlobalVector(T_Mode, new Vector4(sx, sy, p.TPhase, 1));
                    cmd.SetGlobalVector(T_BL, p.TPrevBL); cmd.SetGlobalVector(T_Rx, p.TPrevRx);
                    cmd.SetGlobalVector(T_Uy, p.TPrevUy); cmd.SetGlobalVector(T_F, p.TPrevF);
                    cmd.SetGlobalVector(T_Move, move);
                    cmd.Blit((Texture)null, p.TFresh, mat, 16);
                    cmd.SetGlobalTexture(T_Fresh, p.TFresh);
                    cmd.SetGlobalTexture(T_Hist, prev);
                    cmd.Blit((Texture)null, cur, mat, 17);
                }
            }
            p.THistOk = true; p.THistIdx ^= 1; p.TCam = cam; p.TLastFrame = Time.frameCount;
            p.TPrevBL = p.TNowBL; p.TPrevRx = p.TNowRx; p.TPrevUy = p.TNowUy; p.TPrevF = p.TNowF; p.TPrevPos = p.TNowPos;
            return cur;
        }

        public CloudPass(NOCloudsPlugin p)
        {
            _p = p;
            renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
            ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            ref var cd = ref renderingData.cameraData;
            Camera cam = cd.camera;
            var mat = _p.Prepare(cam, cd.isHdrEnabled, out int ds);
            bool refl = cam.cameraType == CameraType.Reflection;
            bool ownW = !refl && _p.WaterOwned && renderPassEvent == RenderPassEvent.BeforeRenderingTransparents;
            if (mat == null)
            {
                if (ownW)
                {
                    var cw = CommandBufferPool.Get("NOClouds water");
                    cw.SetRenderTarget(cd.renderer.cameraColorTargetHandle, cd.renderer.cameraDepthTargetHandle);
                    _p.DrawWater(cw);
                    context.ExecuteCommandBuffer(cw); CommandBufferPool.Release(cw);
                }
                return;
            }
            var cmd = CommandBufferPool.Get("NOClouds");
            using (new ProfilingScope(cmd, refl ? Perf.Refl : Perf.Clouds))
            {
                var desc = cd.cameraTargetDescriptor;
                int w = Mathf.Max(1, desc.width / ds), h = Mathf.Max(1, desc.height / ds);
                // weather maps are centered on the camera: the probe is elsewhere, so rebuild them for it and
                // force the main camera to rebuild its own afterwards
                _p.EnsureWeather(cmd, mat, refl);
                bool temporal = !refl && _p.TemporalMode > 0 && TemporalSupported(mat);
                var rt = refl ? _p.ReflCloudRT : _p.CloudRT;
                if (temporal) rt = null;
                else if (rt == null || rt.width != w || rt.height != h)
                {
                    if (rt != null) rt.Release();
                    rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf) { name = refl ? "NOCloudsRT probe" : "NOCloudsRT", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                    rt.Create();
                    if (refl) _p.ReflCloudRT = rt; else _p.CloudRT = rt;
                }
                cmd.SetGlobalVector(ID._NOCTexel, new Vector4(1f / w, 1f / h, w, h));
                // cloud probe at the aircraft (for wet glass), read back without stalling the GPU
                if (!refl && Time.time - _probeT > 0.1f)
                {
                    _probeT = Time.time;
                    if (_p.ProbeRT == null) { _p.ProbeRT = new RenderTexture(2, 1, 0, RenderTextureFormat.ARGBFloat) { name = "NOC probe", filterMode = FilterMode.Point }; _p.ProbeRT.Create(); }
                    cmd.Blit((Texture)null, _p.ProbeRT, mat, 8);
                    if (_p.Wet != null) _p.Wet.ProbeTime = Time.time;
                    if (_onProbe == null) _onProbe = OnProbe;   // one delegate for the session (no garbage every 0.1 s)
                    cmd.RequestAsyncReadback(_p.ProbeRT, _onProbe);
                }
                // cumulonimbus map for lightning (every 3 s)
                if (!refl && _p.Atmos != null && Time.time - _p.Atmos.CbMapTime > 3f)
                {
                    var at = _p.Atmos; at.CbMapTime = Time.time;
                    if (_p.CbRT == null) { _p.CbRT = new RenderTexture(96, 96, 0, RenderTextureFormat.ARGBFloat) { name = "NOC Cb map", filterMode = FilterMode.Point }; _p.CbRT.Create(); }
                    cmd.Blit((Texture)null, _p.CbRT, mat, 9);
                    float ext = _p.WExtNow; Vector2 gd = _p.GDLast; Vector3 cpos = cam.transform.position;
                    cmd.RequestAsyncReadback(_p.CbRT, r => { if (!r.hasError) at.OnCbMap(r.GetData<Color>(), 96, ext, gd, cpos); });
                }
                if (temporal) rt = MarchTemporal(cmd, cam, mat, w, h);
                else using (new ProfilingScope(cmd, refl ? null : Perf.March)) cmd.Blit((Texture)null, rt, mat, 0);
                if (!refl) { _p.THistOk &= temporal; _p.CloudImg = rt; _p.RTFrame = Time.frameCount; }
                if (ownW)
                {
                    // sea first (with the camera depth), then the clouds on top of everything
                    cmd.SetRenderTarget(cd.renderer.cameraColorTargetHandle, cd.renderer.cameraDepthTargetHandle);
                    _p.DrawWater(cmd);
                }
                cmd.SetGlobalFloat(WaterMode, (refl || ownW || renderPassEvent == RenderPassEvent.AfterRenderingTransparents) ? -1f : 0f);
                cmd.Blit(rt, cd.renderer.cameraColorTargetHandle, mat, 1);
                if (_p.FogExtra > 1e-6f && (ownW || renderPassEvent == RenderPassEvent.AfterRenderingTransparents))
                    cmd.Blit((Texture)null, cd.renderer.cameraColorTargetHandle, mat, 14);
                // URP's color copy (_CameraOpaqueTexture) is taken before the sea and clouds; afterburner heat haze
                // and other refraction effects read it and showed empty sky behind the flame (blue-white trail).
                // Copy the image again, now with sea, clouds and fog.
                if (!refl && cd.requiresOpaqueTexture && renderPassEvent == RenderPassEvent.BeforeRenderingTransparents)
                {
                    // the copy's RTHandle lives in a private field of UniversalRenderer (Shader.GetGlobalTexture returned null)
                    var oh = F_opaque?.GetValue(cd.renderer) as RTHandle;
                    if (oh != null && oh.rt != null) cmd.Blit(cd.renderer.cameraColorTargetHandle, oh);
                    if (!_opqLogged) { _opqLogged = true; NOCloudsPlugin.Log.LogInfo("Opaque texture: " + (oh != null && oh.rt != null ? oh.rt.width + "x" + oh.rt.height + " recopied after sea+clouds" : "not found")); }
                }
                // restore the camera color AND depth: URP caches the target and the transparents (water) come right after
                cmd.SetRenderTarget(cd.renderer.cameraColorTargetHandle, cd.renderer.cameraDepthTargetHandle);
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }

    // Before opaques: build the cloud shadow map and put it in place of the sun cookie (as HDRP/URP do).
    class ShadowPass : ScriptableRenderPass
    {
        readonly NOCloudsPlugin _p;
        static readonly int MainTex = Shader.PropertyToID("_MainLightCookieTexture");
        static readonly int MainW2L = Shader.PropertyToID("_MainLightWorldToLight");
        static readonly int MainFmt = Shader.PropertyToID("_MainLightCookieTextureFormat");
        public ShadowPass(NOCloudsPlugin p) { _p = p; renderPassEvent = RenderPassEvent.BeforeRenderingOpaques; }
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            ref var cd = ref renderingData.cameraData;
            var mat = _p.Prepare(cd.camera, cd.isHdrEnabled, out int _);
            if (mat == null) return;
            if (!_p.PrepareShadow(cd.camera, mat, out var fr)) { _p.ShCookie.ok = _p.ShDepth.ok = false; return; }
            int res = fr.res;
            var cmd = CommandBufferPool.Get("NOClouds shadow");
            using (new ProfilingScope(cmd, Perf.Shadow))
            {
                _p.EnsureWeather(cmd, mat);
                // the cookie and the opacity depth map take turns (ShadowUpdateEvery); both are redrawn at once
                // after a jump (spawn, camera switch), a resolution change or a sun change
                int k = _p.ShEvery, ph = Time.frameCount % k;
                bool Stale(NOCloudsPlugin.ShFrame o) => !o.ok || o.res != res || o.S != fr.S || Quaternion.Angle(o.q, fr.q) > 0.5f
                                                      || (o.centerG - fr.centerG).sqrMagnitude > fr.S * fr.S * 0.0025f;
                bool doCookie = ph == 0 || Stale(_p.ShCookie);
                bool doDepth = ph == k / 2 || Stale(_p.ShDepth);
                var rt = _p.ShadowRT;
                if (rt == null || rt.width != res)
                {
                    if (rt != null) rt.Release();
                    rt = _p.ShadowRT = new RenderTexture(res, res, 0, RenderTextureFormat.R8) { name = "NOC_Shadow", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                    rt.Create(); doCookie = true;
                }
                if (doCookie)
                {
                    NOCloudsPlugin.ApplyShFrame(cmd, cd.camera, fr);
                    cmd.Blit((Texture)null, rt, mat, 6);
                    _p.ShCookie = fr;
                }
                var rd = _p.ShadowDRT;
                if (rd == null || rd.width != res)
                {
                    if (rd != null) rd.Release();
                    rd = _p.ShadowDRT = new RenderTexture(res, res, 0, RenderTextureFormat.RFloat) { name = "NOC_ShadowDepth", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                    rd.Create(); doDepth = true;
                }
                if (doDepth)
                {
                    NOCloudsPlugin.ApplyShFrame(cmd, cd.camera, fr);
                    cmd.Blit((Texture)null, rd, mat, 7);
                    _p.ShDepth = fr;
                }
                // the rest of the frame (cloud lighting, light shafts) reads the depth map with its own frame
                NOCloudsPlugin.ApplyShFrame(cmd, cd.camera, _p.ShDepth);
                cmd.SetGlobalTexture(ID._NOCShD, rd);
                _p.ShDFrame = Time.frameCount; _p.ShDCam = cd.camera;
                var w2c = NOCloudsPlugin.CookieMatrix(_p.ShCookie);
                cmd.SetGlobalTexture(MainTex, rt);
                cmd.SetGlobalMatrix(MainW2L, w2c);
                cmd.SetGlobalFloat(MainFmt, 2f);   // "Red" format
                cmd.EnableShaderKeyword("_LIGHT_COOKIES");
                cmd.SetRenderTarget(cd.renderer.cameraColorTargetHandle, cd.renderer.cameraDepthTargetHandle);
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }

    // After transparents: recomposite the clouds only on pixels where the ray hits water before the terrain.
    class WaterPass : ScriptableRenderPass
    {
        readonly NOCloudsPlugin _p;
        public WaterPass(NOCloudsPlugin p)
        {
            _p = p;
            renderPassEvent = RenderPassEvent.AfterRenderingTransparents;
            ConfigureInput(ScriptableRenderPassInput.Depth);
        }
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            var rt = _p.CloudImg;
            var mat = _p.Mat;
            if (rt == null || mat == null || _p.RTFrame != Time.frameCount) return;
            var cmd = CommandBufferPool.Get("NOClouds water");
            using (new ProfilingScope(cmd, Perf.Water))
            {
                cmd.SetGlobalFloat(CloudPass.WaterMode, 1f);
                cmd.Blit(rt, renderingData.cameraData.renderer.cameraColorTargetHandle, mat, 1);
                cmd.SetRenderTarget(renderingData.cameraData.renderer.cameraColorTargetHandle, renderingData.cameraData.renderer.cameraDepthTargetHandle);
                cmd.SetGlobalFloat(CloudPass.WaterMode, 0f);
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }
}
