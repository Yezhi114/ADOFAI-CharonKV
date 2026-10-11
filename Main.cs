using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using UnityModManagerNet;

namespace CharonKV
{
    // 🟢 定义配置类
    [System.Serializable]
    public class KVSettings
    {
        public float positionX = 0;
        public float positionY = 0;
        public float sizeX = 400;
        // 🟢【1.3.0】按键映射：默认全部"未设置"（KeyCode.None），首次使用需在设置界面逐个注册按键
        public KeyCode[] keyBindings = new KeyCode[16];
        // 🟢【1.3.0】界面语言："zh" / "en"
        public string language = "zh";
        // 🟢【1.3.0】Head 触发键数：16 / 8 / 6 / 4
        public int headKeyCount = 8;
    }

    public class Main
    {
        // 🟢【优化】角色状态常量（避免魔法字符串散落各处，拼写错误会静默失效）
        private const string STATE_CHARON = "charon";
        private const string STATE_CLEAR = "clear";
        private const string STATE_DIE = "die";
        private const string STATE_HEAD = "charon_head";
        private const string STATE_WINK = "charon_wink";

        // 🟢【优化】可绑定按键列表缓存：一次性枚举（排除 None 与鼠标键），
        // 避免改键等待期间每帧 Enum.GetValues 分配数组与装箱
        private static readonly KeyCode[] bindableKeyCodes = BuildBindableKeyCodes();
        static KeyCode[] BuildBindableKeyCodes()
        {
            List<KeyCode> list = new List<KeyCode>();
            foreach (KeyCode kc in System.Enum.GetValues(typeof(KeyCode)))
            {
                if (kc == KeyCode.None) continue;
                if (kc.ToString().StartsWith("Mouse")) continue;
                list.Add(kc);
            }
            return list.ToArray();
        }

        public static UnityModManager.ModEntry.ModLogger logger;
        public static UnityModManager.ModEntry modEntry;
        public static KVSettings settings = new KVSettings();

        // 🟢【修复】滑块等配置有改动但尚未写盘的标记（松开鼠标时才保存）
        private static bool settingsDirty = false;

        // 🟢【1.3.0】设置项高亮颜色：DodgerBlue（RGB 30,144,255 / #1E90FF）
        private static readonly Color highlightColor = new Color(30f / 255f, 144f / 255f, 255f / 255f, 1f);

        // 🟢【优化】位置/尺寸应用缓存：设置值未变化时跳过重算（OnGUI 每个事件都会调用 UpdatePositionAndSize）
        private static float appliedPosX = float.NaN;
        private static float appliedPosY = float.NaN;
        private static float appliedSizeX = -1f;
        // 🟢【优化】Image 组件缓存（避免 UpdatePositionAndSize 每次 GetComponent）
        private static Image pianoImage;
        private static Image charonImage;

        private static GameObject kvCanvas;
        private static GameObject pianoObj;
        private static GameObject charonObj;
        private static GameObject leftHandObj;
        private static GameObject rightHandObj;
        private static Dictionary<string, Sprite> spriteDict = new Dictionary<string, Sprite>();

        // 🟢 手部保留缓存（为了不掉帧）
        private static Image leftHandImage;
        private static Image rightHandImage;

        // 🟢 UI 缓存（为了不掉帧）
        private static scrUIController cachedUIController = null;

        // 🟢 新增：按键高亮图层（key1 ~ key16）
        private static GameObject[] keyObjects = new GameObject[16];

        // 眨眼控制变量
        private static float blinkTimer = 0f;
        private static float nextBlinkTime = 5f;
        private static bool isBlinking = false;
        private static float blinkDuration = 0.1f;

        private static int leftHandIndex = -1;
        private static int rightHandIndex = -1;

        private static int waitingKeyIndex = -1;
        private static bool isHeadMode = false;

        private static string currentCharonState = STATE_CHARON;
        private static string lastLoadedSceneName = "";
        // 🟢【补丁2】加入这个变量，用于在场景刚加载时防误触
        private static float sceneLoadTimer = 0f;
        // 🟢【优化】帧号时间戳改 long：int 在 60fps 下约 414 天后溢出，long 可视为永不溢出
        private static long frameCounter = 0;
        private static bool[] prevKeyState = new bool[16];
        private static long[] keyPressFrame = new long[16];
        // 🟢【修复】复用按键状态缓冲区（不再每帧 new bool[16]）
        private static bool[] currentKeyState = new bool[16];
        // 🟢【修复】按键/手部贴图缓存（避免每帧字符串拼接和字典查找）
        private static Sprite[] pressedKeySprites = null;
        private static Sprite unpressedLeftSprite = null;
        private static Sprite unpressedRightSprite = null;
        // 🟢 记录上一次进入的游戏关卡场景名（用于区分结算和编辑）
        private static string previousGameSceneName = "";

        // 🟢【1.3.0】双语辅助：根据当前语言返回中文/英文文本
        static string L(string zh, string en)
        {
            return settings.language == "en" ? en : zh;
        }

        static void LoadSettings()
        {
            string path = modEntry.Path + "Config.json";
            if (File.Exists(path))
            {
                try
                {
                    string json = File.ReadAllText(path);
                    settings = JsonUtility.FromJson<KVSettings>(json);
                    // 🟢【修复】配置健壮性：反序列化失败或按键绑定数量异常时回退默认，避免 keyBindings 越界崩溃
                    if (settings == null)
                    {
                        settings = new KVSettings();
                    }
                    if (settings.keyBindings == null || settings.keyBindings.Length != 16)
                    {
                        settings.keyBindings = new KVSettings().keyBindings;
                        logger.Log("配置文件按键绑定数量异常，已重置为未设置状态。");
                    }
                    // 🟢【1.3.0】新字段白名单校验：非法值回退默认，避免玩家手改 JSON 导致界面/逻辑异常
                    if (settings.language != "zh" && settings.language != "en")
                    {
                        settings.language = "zh";
                        logger.Log("配置文件语言设置异常，已重置为中文。");
                    }
                    if (settings.headKeyCount != 16 && settings.headKeyCount != 8 &&
                        settings.headKeyCount != 6 && settings.headKeyCount != 4)
                    {
                        settings.headKeyCount = 8;
                        logger.Log("配置文件 Head 触发键数异常，已重置为 8 键。");
                    }
                    logger.Log("成功读取本地配置！");
                }
                catch (System.Exception e)
                {
                    logger.Log($"读取配置失败，将使用默认配置: {e.Message}");
                }
            }
            else
            {
                logger.Log("未找到配置文件，使用默认配置。");
            }
        }

        static void SaveSettings()
        {
            try
            {
                string path = modEntry.Path + "Config.json";
                string json = JsonUtility.ToJson(settings, true);
                File.WriteAllText(path, json);
                logger.Log("成功保存配置！");
            }
            catch (System.Exception e)
            {
                logger.Log($"保存配置失败: {e.Message}");
            }
        }

        static bool Load(UnityModManager.ModEntry modEntryObj)
        {
            logger = modEntryObj.Logger;
            modEntry = modEntryObj;
            LoadSettings();
            modEntryObj.OnToggle = OnToggle;
            return true;
        }

        static bool OnToggle(UnityModManager.ModEntry modEntryObj, bool value)
        {
            if (value)
            {
                logger.Log("mod已启用");
                modEntryObj.OnGUI += OnGUI;
                modEntryObj.OnUpdate += OnUpdate;
                SceneManager.sceneLoaded += OnSceneLoaded;

                // 🟢【修复】启用模组时重置按键跟踪，避免上次残留状态导致手部显示错误
                for (int i = 0; i < 16; i++)
                {
                    prevKeyState[i] = false;
                    keyPressFrame[i] = 0;
                }
                // 🟢 启用时同步一次当前场景名，供通关检测使用
                lastLoadedSceneName = SceneManager.GetActiveScene().name;
                if (SceneManager.GetActiveScene().isLoaded)
                {
                    CreateKVUI();
                }
            }
            else
            {
                logger.Log("mod已禁用");
                modEntryObj.OnGUI -= OnGUI;
                modEntryObj.OnUpdate -= OnUpdate;
                SceneManager.sceneLoaded -= OnSceneLoaded;
                DestroyKVUI();
                SaveSettings();
            }
            return true;
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // 🟢 如果当前加载的是游戏关卡，记录下来供结算判断使用
            if (scene.name.Contains("LoadLevel") || scene.name == "scnGame")
            {
                previousGameSceneName = scene.name;
            }
            // 🟢 进入与关卡/结算无关的场景（如主菜单）时清空记录，避免菜单→编辑器路径误触发 clear
            else if (scene.name != "scnEditor")
            {
                previousGameSceneName = "";
            }

            // 🟢【补丁3】场景加载时，重置计时器
            sceneLoadTimer = 0f;

            // 🟢【修复】场景切换/重载时重置按键跟踪，避免按住键重试时手部显示错误
            for (int i = 0; i < 16; i++)
            {
                prevKeyState[i] = false;
                keyPressFrame[i] = 0;
            }

            lastLoadedSceneName = scene.name;
            cachedUIController = UnityEngine.Object.FindFirstObjectByType<scrUIController>();

            // 🟢【补丁3】重点：如果当前是 clear，且加载的是结算/编辑器场景，绝对不要覆盖状态！
            if (scene.name == "scnEditor" && currentCharonState == STATE_CLEAR)
            {
                // 什么都不做，直接跳出，完美保留 clear 图片！
                return;
            }

            // 🟢【补丁3】如果是进入新关卡或死亡重载，强制重置为 charon 并恢复手
            if (scene.name.Contains("LoadLevel") || scene.name == "scnGame")
            {
                currentCharonState = STATE_CHARON;
                SetCharonImage(STATE_CHARON);
                leftHandObj?.SetActive(true);
                rightHandObj?.SetActive(true);
                if (leftHandImage != null && spriteDict.ContainsKey("unpressed_left"))
                    leftHandImage.sprite = spriteDict["unpressed_left"];
                if (rightHandImage != null && spriteDict.ContainsKey("unpressed_right"))
                    rightHandImage.sprite = spriteDict["unpressed_right"];
            }
            // 🟢【补丁5】离开游玩场景（回主菜单 / 进编辑器 / 进选关等）时，
            // 状态还卡在 clear 或 die 就重置掉：clear 防止结算图残留；
            // die 防止 scnGame 死亡后切到编辑器（游戏是附加加载 scnEditor，scnGame 并不卸载）把 die 锁死
            else if (currentCharonState == STATE_CLEAR || currentCharonState == STATE_DIE)
            {
                currentCharonState = STATE_CHARON;
                SetCharonImage(STATE_CHARON);
                leftHandObj?.SetActive(true);
                rightHandObj?.SetActive(true);
                if (leftHandImage != null && spriteDict.ContainsKey("unpressed_left"))
                    leftHandImage.sprite = spriteDict["unpressed_left"];
                if (rightHandImage != null && spriteDict.ContainsKey("unpressed_right"))
                    rightHandImage.sprite = spriteDict["unpressed_right"];
            }
            CreateKVUI();
        }

        static void SetCharonImage(string spriteName)
        {
            if (charonObj == null || !spriteDict.ContainsKey(spriteName)) return;
            Image img = charonObj.GetComponent<Image>();
            if (img != null)
            {
                img.sprite = spriteDict[spriteName];
                currentCharonState = spriteName;
            }
        }

        // 🟢【修复】判断当前状态是否属于游戏定义的死亡状态（优先用 scrController.deathStates，兜底 Fail/Fail2）
        static bool IsDeathState(States state)
        {
            if (scrController.deathStates != null)
            {
                for (int i = 0; i < scrController.deathStates.Length; i++)
                {
                    if (scrController.deathStates[i] == state) return true;
                }
                return false;
            }
            return state == States.Fail || state == States.Fail2;
        }

        static void OnUpdate(UnityModManager.ModEntry modEntry, float deltaTime)
        {
            // 🟢【补丁4】每帧累加时间，用于防误触
            sceneLoadTimer += deltaTime;

            if (kvCanvas == null) return;

            // 1. 按键绑定逻辑
            if (waitingKeyIndex != -1)
            {
                // 🟢【优化】遍历缓存的按键列表（已排除 None 与鼠标键），避免每帧 GetValues 分配
                for (int k = 0; k < bindableKeyCodes.Length; k++)
                {
                    KeyCode kc = bindableKeyCodes[k];
                    if (Input.GetKeyDown(kc))
                    {
                        settings.keyBindings[waitingKeyIndex] = kc;
                        logger.Log($"按键已更新为: {kc.ToString()}");
                        SaveSettings();
                        waitingKeyIndex = -1;
                        break;
                    }
                }
            }

            // 🟢【修复】每帧只轮询一次按键状态，供按键高亮/手部/Head和弦共用（约 40 次 Input.GetKey 减到 16 次）
            // 🟢【1.3.0】未设置的键（KeyCode.None）直接视为未按下，不参与轮询
            for (int i = 0; i < 16; i++)
            {
                currentKeyState[i] = settings.keyBindings[i] != KeyCode.None && Input.GetKey(settings.keyBindings[i]);
            }

            // 🟢 按键高亮：直接复用本帧轮询结果
            for (int i = 0; i < 16; i++)
            {
                if (keyObjects[i] != null && keyObjects[i].activeSelf != currentKeyState[i])
                {
                    keyObjects[i].SetActive(currentKeyState[i]);
                }
            }

            // 2. 手部动作逻辑（时间戳版，完美处理顺序问题，同时不丢失高频）
            frameCounter++; // 每帧递增，作为时间戳

            // 检测新按下：如果当前按下且上一帧未按下，则记录时间戳
            for (int i = 0; i < 16; i++)
            {
                if (currentKeyState[i] && !prevKeyState[i])
                {
                    keyPressFrame[i] = frameCounter;
                }
            }

            // 选择左右手各自时间戳最大的那个键（即最后按下的）
            int bestLeftIndex = -1;
            int bestRightIndex = -1;
            long maxLeftFrame = 0;
            long maxRightFrame = 0;

            for (int i = 0; i < 16; i++)
            {
                if (!currentKeyState[i]) continue;

                bool isLeft = (i >= 4 && i <= 7) || (i >= 12 && i <= 15);
                bool isRight = (i <= 3 || (i >= 8 && i <= 11));

                if (isLeft && keyPressFrame[i] > maxLeftFrame)
                {
                    maxLeftFrame = keyPressFrame[i];
                    bestLeftIndex = i;
                }
                else if (isRight && keyPressFrame[i] > maxRightFrame)
                {
                    maxRightFrame = keyPressFrame[i];
                    bestRightIndex = i;
                }
            }

            // 更新全局索引（注意：这里直接赋值给 leftHandIndex 和 rightHandIndex，没有 new 前缀）
            leftHandIndex = bestLeftIndex;
            rightHandIndex = bestRightIndex;

            // 根据索引更新左右手图片（直接使用预缓存的贴图，不再每帧拼接字符串和查字典）
            if (!isHeadMode)
            {
                if (leftHandImage != null)
                {
                    if (leftHandIndex != -1 && pressedKeySprites != null && leftHandIndex < pressedKeySprites.Length && pressedKeySprites[leftHandIndex] != null)
                    {
                        leftHandImage.sprite = pressedKeySprites[leftHandIndex];
                    }
                    else if (unpressedLeftSprite != null)
                    {
                        leftHandImage.sprite = unpressedLeftSprite;
                    }
                }

                if (rightHandImage != null)
                {
                    if (rightHandIndex != -1 && pressedKeySprites != null && rightHandIndex < pressedKeySprites.Length && pressedKeySprites[rightHandIndex] != null)
                    {
                        rightHandImage.sprite = pressedKeySprites[rightHandIndex];
                    }
                    else if (unpressedRightSprite != null)
                    {
                        rightHandImage.sprite = unpressedRightSprite;
                    }
                }
            }

            // 🟢【1.3.0】Head 特殊模式逻辑：触发键数可配置（16/8/6/4），对应键位数组的一段连续索引
            int headStart = 0;
            int headCount = 8;
            switch (settings.headKeyCount)
            {
                case 16: headCount = 16; break;
                case 8: headCount = 8; break;
                case 6: headStart = 1; headCount = 6; break;  // 显示序号 2~7（双手各3键，中置）
                case 4: headStart = 2; headCount = 4; break;  // 显示序号 3~6（双手各2键，中置）
            }

            bool allHeadKeysPressed = true;
            for (int i = headStart; i < headStart + headCount; i++)
            {
                if (!currentKeyState[i])
                {
                    allHeadKeysPressed = false;
                    break;
                }
            }

            // 🟢【修复】通关(clear)/死亡(die)状态下不触发 Head 和弦，避免干扰结算表现；其他场景一律启用
            bool isClearOrDie = (currentCharonState == STATE_CLEAR || currentCharonState == STATE_DIE);

            if (!isClearOrDie)
            {
                if (allHeadKeysPressed)
                {
                    if (!isHeadMode)
                    {
                        isHeadMode = true;
                        if (leftHandObj != null) leftHandObj.SetActive(false);
                        if (rightHandObj != null) rightHandObj.SetActive(false);
                        if (spriteDict.ContainsKey("charon_head") && charonObj != null)
                        {
                            SetCharonImage(STATE_HEAD);
                        }
                        isBlinking = false;
                    }
                }
                else
                {
                    if (isHeadMode)
                    {
                        isHeadMode = false;
                        if (leftHandObj != null) leftHandObj.SetActive(true);
                        if (rightHandObj != null) rightHandObj.SetActive(true);
                        if (charonObj != null)
                        {
                            SetCharonImage(STATE_CHARON);
                        }
                        blinkTimer = 0f;
                        nextBlinkTime = Random.Range(5f, 10f);
                    }
                }
            }

            // 🟢【修复】双缓冲交换：prev 指向本帧数据，current 复用上一帧的缓冲区（不再每帧 new）
            bool[] swapBuffer = prevKeyState;
            prevKeyState = currentKeyState;
            currentKeyState = swapBuffer;

            // 🟢 4. 死亡与通关检测逻辑
            if (charonObj != null)
            {
                string targetSprite = STATE_CHARON;
                bool deadOrCleared = false;

                // 🟢 通关检测：沿用原来的 txtCongrats 检测（稳定方案，不依赖状态机）
                string currentScene = lastLoadedSceneName;
                bool allowClearCheck = false;
                if (currentScene.Contains("LoadLevel") || currentScene == "scnGame")
                {
                    allowClearCheck = true;
                }
                else if (currentScene == "scnEditor" && (previousGameSceneName.Contains("LoadLevel") || previousGameSceneName == "scnGame"))
                {
                    allowClearCheck = true;
                }

                // 🟢【优化】activeInHierarchy 每帧只取一次（层级遍历有开销），clear 检测与 clear 守卫共用
                bool txtCongratsActive = cachedUIController != null
                    && cachedUIController.txtCongrats != null
                    && cachedUIController.txtCongrats.gameObject.activeInHierarchy;

                if (allowClearCheck && txtCongratsActive)
                {
                    // 加上 1.5 秒冷却
                    if (sceneLoadTimer > 1.5f)
                    {
                        targetSprite = STATE_CLEAR;
                        deadOrCleared = true;
                    }
                }

                // 🟢 死亡检测（三路信号，任一命中即判死亡）：
                //  A. 完成度文本 txtPercent 激活（死亡时显示"完成度X%"，编辑器同样适用），且不在通关结算中
                //  B. 状态机 Fail/Fail2（官谱已验证）
                //  C. 玩家存活兜底 scrController.instance.playerOne.alive == false
                scrController sc = scrController.instance;

                // 🟢【补丁5】死亡检测只在本场景就是游玩场景（scnGame / LoadLevel*）时启用。
                // 从暂停菜单进编辑器时，游戏是附加加载 scnEditor（LoadScene Additive），
                // scnGame 场景并不卸载：scrController 会一直残留 Fail/死亡状态、
                // txtPercent 也可能仍处于激活。若不按场景限制，这些残留信号会把 die 图片锁死在编辑器里。
                bool allowDeathCheck = currentScene.Contains("LoadLevel") || currentScene == "scnGame";

                // 🟢 clear 守卫：通关祝贺文本（txtCongrats）激活时不判死亡
                bool clearTextShowing = txtCongratsActive;

                bool isDeath = false;

                // A. 完成度文本（txtPercent）
                if (allowDeathCheck && !clearTextShowing && cachedUIController != null && cachedUIController.txtPercent != null)
                {
                    if (sceneLoadTimer > 0.5f && cachedUIController.txtPercent.gameObject.activeInHierarchy)
                    {
                        isDeath = true;
                    }
                }

                // B. 状态机（编辑器 States.None 不算死亡）
                if (allowDeathCheck && !isDeath && sc != null && sceneLoadTimer > 0.5f)
                {
                    States st = sc.currentState;
                    if (st != States.None && IsDeathState(st))
                    {
                        isDeath = true;
                    }
                }

                // C. 玩家存活兜底（alive == false）
                if (allowDeathCheck && !isDeath && sc != null && sceneLoadTimer > 0.5f)
                {
                    scrPlayer p = sc.playerOne;
                    if (p != null && !p.alive)
                    {
                        isDeath = true;
                    }
                }

                if (isDeath)
                {
                    // 🟢【修复】死亡：显示 die 图片（状态离开死亡状态后由下方管道自动恢复为 charon）
                    targetSprite = STATE_DIE;
                    deadOrCleared = true;
                }

                // 🟢 死亡/通关时强制退出 8 键头模式，避免状态冲突
                if (deadOrCleared && isHeadMode)
                {
                    isHeadMode = false;
                    isBlinking = false;
                }

                // 🟢 头模式下由 8 键逻辑控制角色贴图，这里不覆盖，避免头模式被 charon 盖掉
                if (!isHeadMode)
                {
                    if (deadOrCleared)
                    {
                        if (leftHandObj != null) leftHandObj.SetActive(false);
                        if (rightHandObj != null) rightHandObj.SetActive(false);
                    }

                    // 🟢 统一走 targetSprite 管道切换贴图：死亡恢复时能真正换回 charon
                    if (currentCharonState != targetSprite && spriteDict.ContainsKey(targetSprite))
                    {
                        SetCharonImage(targetSprite);
                    }

                    if (currentCharonState == STATE_CHARON)
                    {
                        if (leftHandObj != null && !leftHandObj.activeSelf) leftHandObj.SetActive(true);
                        if (rightHandObj != null && !rightHandObj.activeSelf) rightHandObj.SetActive(true);
                    }
                }
            }

            // 5. 眨眼逻辑
            if (charonObj != null && !isHeadMode && currentCharonState == STATE_CHARON)
            {
                blinkTimer += deltaTime;

                if (isBlinking)
                {
                    if (blinkTimer >= blinkDuration)
                    {
                        isBlinking = false;
                        SetCharonImage(STATE_CHARON);
                        nextBlinkTime = Random.Range(5f, 10f);
                        blinkTimer = 0f;
                    }
                }
                else
                {
                    if (blinkTimer >= nextBlinkTime)
                    {
                        isBlinking = true;
                        SetCharonImage(STATE_WINK);
                        blinkTimer = 0f;
                    }
                }
            }
        }

        static void CreateKVUI()
        {
            logger.Log("🚀 CreateKVUI 正在执行，尝试创建 KV");

            if (kvCanvas != null) return;
            LoadSprites();

            GameObject canvasGO = new GameObject("CharonKV_Canvas");
            kvCanvas = canvasGO;

            // 🟢【补丁1】添加这一行：让 KV 跨越任何场景切换都不被销毁
            UnityEngine.Object.DontDestroyOnLoad(canvasGO);

            Canvas canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasGO.AddComponent<CanvasScaler>();
            canvasGO.AddComponent<GraphicRaycaster>();

            // 🟢 核心修正！严格按照正确图层顺序创建：

            // 1. 最底层：钢琴
            pianoObj = CreateImage("piano", canvasGO.transform);

            // 🟢 2. 第二层：角色（Charon 必须在钢琴之上，按键高亮之下）
            charonObj = CreateImage("charon", canvasGO.transform);

            // 🟢 3. 第三层：16个按键高亮（盖住钢琴的按键，形成发光效果）
            for (int i = 0; i < 16; i++)
            {
                string keyName = "key" + (i + 1);
                GameObject keyGo = CreateImage(keyName, pianoObj.transform);
                keyGo.SetActive(false); // 默认隐藏
                keyObjects[i] = keyGo;
            }

            // 4. 最上层：左右手（如果需要手遮住按键，保持后建即可，目前逻辑没问题）
            leftHandObj = CreateImage("unpressed_left", canvasGO.transform);
            rightHandObj = CreateImage("unpressed_right", canvasGO.transform);

            // 🟢 建立手部缓存（防掉帧关键）
            leftHandImage = leftHandObj.GetComponent<Image>();
            rightHandImage = rightHandObj.GetComponent<Image>();

            UpdatePositionAndSize();

            // 🟢【临时修复】强制确保 Canvas 处于激活状态
            if (kvCanvas != null) kvCanvas.SetActive(true);
        }

        static void LoadSprites()
        {
            logger.Log("开始从嵌入式资源加载图片...");
            // 🟢 修复：加入了 key1 到 key16 的加载
            string[] imageNames = new string[] {
                "charon", "charon_wink", "charon_head",
                "clear", "die", "piano",
                "key1", "key2", "key3", "key4", "key5", "key6", "key7", "key8",
                "key9", "key10", "key11", "key12", "key13", "key14", "key15", "key16",
                "pressed_key1", "pressed_key2", "pressed_key3", "pressed_key4",
                "pressed_key5", "pressed_key6", "pressed_key7", "pressed_key8",
                "pressed_key9", "pressed_key10", "pressed_key11", "pressed_key12",
                "pressed_key13", "pressed_key14", "pressed_key15", "pressed_key16",
                "unpressed_left", "unpressed_right"
            };

            foreach (string name in imageNames)
            {
                try
                {
                    string resourceName = "CharonKV.Resources." + name + ".png";
                    using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
                    {
                        if (stream == null)
                        {
                            logger.Log($"加载失败(未找到嵌入式资源): {resourceName}");
                            continue;
                        }
                        using (MemoryStream ms = new MemoryStream())
                        {
                            stream.CopyTo(ms);
                            byte[] fileData = ms.ToArray();

                            // 🟢【修复】用 LockBits 一次性读取像素（替代逐像素 GetPixel，速度大幅提升），并用 using 正确释放 Bitmap/GDI+ 资源
                            using (System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(new MemoryStream(fileData)))
                            {
                                System.Drawing.Rectangle rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
                                System.Drawing.Imaging.BitmapData data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                                try
                                {
                                    byte[] raw = new byte[data.Stride * data.Height];
                                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0, raw, 0, raw.Length);

                                    Texture2D tex = new Texture2D(bmp.Width, bmp.Height, TextureFormat.RGBA32, false);
                                    Color32[] pixels = new Color32[bmp.Width * bmp.Height];
                                    for (int y = 0; y < bmp.Height; y++)
                                    {
                                        int srcBase = y * data.Stride;
                                        int dstBase = (bmp.Height - 1 - y) * bmp.Width; // 翻转 Y：GDI+ 从上到下 → Unity 从下到上
                                        for (int x = 0; x < bmp.Width; x++)
                                        {
                                            int src = srcBase + x * 4;
                                            // Format32bppArgb 内存顺序为 BGRA
                                            pixels[dstBase + x] = new Color32(raw[src + 2], raw[src + 1], raw[src], raw[src + 3]);
                                        }
                                    }
                                    tex.SetPixels32(pixels);
                                    tex.Apply();
                                    Sprite sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                                    spriteDict[name] = sprite;
                                    logger.Log($"加载成功: {name}");
                                }
                                finally
                                {
                                    if (data != null) bmp.UnlockBits(data);
                                }
                            }
                        }
                    }
                }
                catch (System.Exception e)
                {
                    logger.Log($"加载失败: {name} - {e.Message}");
                }
            }

            // 🟢【修复】预缓存按键/手部贴图，OnUpdate 直接引用，避免每帧字符串拼接和字典查找
            pressedKeySprites = new Sprite[16];
            for (int i = 0; i < 16; i++)
            {
                Sprite s;
                if (spriteDict.TryGetValue("pressed_key" + (i + 1), out s))
                {
                    pressedKeySprites[i] = s;
                }
            }
            spriteDict.TryGetValue("unpressed_left", out unpressedLeftSprite);
            spriteDict.TryGetValue("unpressed_right", out unpressedRightSprite);
        }

        static GameObject CreateImage(string spriteName, Transform parent)
        {
            GameObject go = new GameObject(spriteName);
            go.transform.SetParent(parent, false);
            RectTransform rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            Image img = go.AddComponent<Image>();
            if (spriteDict.ContainsKey(spriteName))
            {
                img.sprite = spriteDict[spriteName];
                img.preserveAspect = true;
                rect.localRotation = Quaternion.Euler(0, 0, 0);
            }
            else
            {
                logger.Log($"警告: 找不到图片 {spriteName}");
            }
            return go;
        }

        static void UpdatePositionAndSize()
        {
            if (kvCanvas == null) return;
            if (!spriteDict.ContainsKey("piano") || pianoObj == null || charonObj == null) return;

            // 🟢【优化】位置/大小未变化时直接跳过（OnGUI 每个事件都会调用本方法，绝大多数事件无变化）
            if (appliedPosX == settings.positionX && appliedPosY == settings.positionY && appliedSizeX == settings.sizeX)
            {
                return;
            }

            // 🟢【优化】Image 组件缓存，避免每次 GetComponent
            if (pianoImage == null) pianoImage = pianoObj.GetComponent<Image>();
            if (charonImage == null) charonImage = charonObj.GetComponent<Image>();
            if (leftHandImage == null && leftHandObj != null) leftHandImage = leftHandObj.GetComponent<Image>();
            if (rightHandImage == null && rightHandObj != null) rightHandImage = rightHandObj.GetComponent<Image>();

            pianoImage.SetNativeSize();
            charonImage.SetNativeSize();
            // 🟢【修复】左右手可能为空，加上空判防止 OnGUI 每帧调用时空引用
            if (leftHandImage != null) leftHandImage.SetNativeSize();
            if (rightHandImage != null) rightHandImage.SetNativeSize();

            float baseWidth = spriteDict["piano"].rect.width;
            float baseHeight = spriteDict["piano"].rect.height;
            float scale = settings.sizeX / baseWidth;
            float finalWidth = baseWidth * scale;
            float finalHeight = baseHeight * scale;

            SetRectTransform(pianoObj, settings.positionX, settings.positionY, finalWidth, finalHeight);
            SetRectTransform(charonObj, settings.positionX, settings.positionY, finalWidth, finalHeight);
            SetRectTransform(leftHandObj, settings.positionX, settings.positionY, finalWidth, finalHeight);
            SetRectTransform(rightHandObj, settings.positionX, settings.positionY, finalWidth, finalHeight);

            // 🟢 新增：让按键高亮层 100% 覆盖在钢琴图上（作为子物体，坐标偏移为0）
            for (int i = 0; i < 16; i++)
            {
                if (keyObjects[i] != null)
                {
                    RectTransform rect = keyObjects[i].GetComponent<RectTransform>();
                    rect.anchoredPosition = Vector2.zero; // 相对于钢琴 0, 0 偏移
                    rect.sizeDelta = new Vector2(finalWidth, finalHeight);
                }
            }

            // 🟢【优化】记录本次应用的值，供下帧跳过判断
            appliedPosX = settings.positionX;
            appliedPosY = settings.positionY;
            appliedSizeX = settings.sizeX;
        }

        static void SetRectTransform(GameObject obj, float x, float y, float w, float h)
        {
            if (obj == null) return;
            RectTransform rect = obj.GetComponent<RectTransform>();
            rect.anchoredPosition = new Vector2(x, y);
            rect.sizeDelta = new Vector2(w, h);
        }

        static void DestroyKVUI()
        {
            if (kvCanvas != null)
            {
                GameObject.Destroy(kvCanvas);
                kvCanvas = null;
                pianoObj = null;
                charonObj = null;
                leftHandObj = null;
                rightHandObj = null;
                // 🟢 清空数组
                for (int i = 0; i < 16; i++) keyObjects[i] = null;
                // 🟢【优化】显式销毁运行时创建的 Sprite 与 Texture2D，释放 GPU 显存（避免反复开关 mod 累积）
                foreach (KeyValuePair<string, Sprite> kv in spriteDict)
                {
                    if (kv.Value == null) continue;
                    Texture2D tex = kv.Value.texture;
                    if (tex != null) GameObject.Destroy(tex);
                    GameObject.Destroy(kv.Value);
                }
                spriteDict.Clear();
                // 🟢【修复】清空贴图缓存引用
                pressedKeySprites = null;
                unpressedLeftSprite = null;
                unpressedRightSprite = null;
                // 🟢【优化】清空组件/UI 缓存引用，避免悬挂引用
                leftHandImage = null;
                rightHandImage = null;
                cachedUIController = null;
                pianoImage = null;
                charonImage = null;
                leftHandIndex = -1;
                rightHandIndex = -1;
                isHeadMode = false;
            }
        }

        static void OnGUI(UnityModManager.ModEntry modentry)
        {
            // 🟢【1.3.0】语言切换（置顶）：点击立即写盘，DodgerBlue 高亮当前语言；两个按钮统一固定高度，避免同行元素高度不一致
            GUILayout.BeginHorizontal();
            GUILayout.Label("语言 / Language:", GUILayout.Height(20));
            GUI.backgroundColor = (settings.language != "en") ? highlightColor : Color.white;
            if (GUILayout.Button("中文", GUILayout.Height(40)))
            {
                settings.language = "zh";
                SaveSettings();
                settingsDirty = false;
            }
            GUI.backgroundColor = (settings.language == "en") ? highlightColor : Color.white;
            if (GUILayout.Button("English", GUILayout.Height(40)))
            {
                settings.language = "en";
                SaveSettings();
                settingsDirty = false;
            }
            GUI.backgroundColor = Color.white;
            GUILayout.EndHorizontal();

            GUILayout.Space(10);
            GUILayout.Label(L("KV 位置设置：", "KV Position:"));
            GUILayout.Space(2);
            GUILayout.Label(L("X 位置：", "X Position:") + settings.positionX);
            float newX = GUILayout.HorizontalSlider(settings.positionX, -2000, 2000);
            if (newX != settings.positionX) { settings.positionX = newX; settingsDirty = true; }
            GUILayout.Label(L("Y 位置：", "Y Position:") + settings.positionY);
            float newY = GUILayout.HorizontalSlider(settings.positionY, -2000, 2000);
            if (newY != settings.positionY) { settings.positionY = newY; settingsDirty = true; }
            GUILayout.Space(10);
            GUILayout.Label(L("KV 大小：", "KV Size:") + settings.sizeX);
            GUILayout.Space(2);
            float newSize = GUILayout.HorizontalSlider(settings.sizeX, 100, 1500);
            if (newSize != settings.sizeX) { settings.sizeX = newSize; settingsDirty = true; }

            // 🟢【1.3.0】Head 触发键数选择：点击立即写盘，DodgerBlue 高亮当前选项
            GUILayout.Space(10);
            GUILayout.Label(L("Head 触发键数：", "Head trigger keys:"));
            GUILayout.BeginHorizontal();
            int[] headChoices = { 16, 8, 6, 4 };
            for (int c = 0; c < headChoices.Length; c++)
            {
                int n = headChoices[c];
                GUI.backgroundColor = (settings.headKeyCount == n) ? highlightColor : Color.white;
                if (GUILayout.Button(n + L(" 键", " keys")))
                {
                    settings.headKeyCount = n;
                    SaveSettings();
                    settingsDirty = false;
                }
                GUI.backgroundColor = Color.white;
            }
            GUILayout.EndHorizontal();

            UpdatePositionAndSize();

            // 🟢【修复】拖动滑块时不立即写盘，松开鼠标（MouseUp）后才保存一次，避免每个 GUI 事件都写配置文件
            if (settingsDirty && Event.current != null && Event.current.type == EventType.MouseUp)
            {
                SaveSettings();
                settingsDirty = false;
            }

            GUILayout.Space(20);
            GUILayout.Label(L("按键绑定设置 (点击键位名称后，直接在键盘敲下新键)：", "Key Bindings (click a key name, then press the new key):"));
            for (int i = 0; i < 16; i++)
            {
                if (i % 8 == 0) GUILayout.BeginHorizontal();
                KeyCode boundKey = settings.keyBindings[i];
                string displayName = (boundKey == KeyCode.None) ? L("未设置", "Unset") : boundKey.ToString();
                bool isWaitingThis = (waitingKeyIndex == i);
                if (isWaitingThis)
                {
                    GUI.backgroundColor = Color.red;
                    displayName = L("等待新键...", "Press a key...");
                }
                else
                {
                    GUI.backgroundColor = Color.white;
                }
                if (GUILayout.Button(displayName, GUILayout.Width(130)))
                {
                    if (isWaitingThis) waitingKeyIndex = -1;
                    else waitingKeyIndex = i;
                }
                GUI.backgroundColor = Color.white;
                if (i % 8 == 7) GUILayout.EndHorizontal();
            }

            // 🟢【1.3.0】重置按钮（拆分并移至最底部）：左侧重置所有设置，右侧只重置按键映射
            GUILayout.Space(10);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(L("重置所有设置", "Reset All Settings")))
            {
                settings = new KVSettings();
                waitingKeyIndex = -1;
                SaveSettings();
                settingsDirty = false;
            }
            if (GUILayout.Button(L("重置按键映射", "Reset Key Bindings")))
            {
                settings.keyBindings = new KVSettings().keyBindings;
                waitingKeyIndex = -1;
                SaveSettings();
                settingsDirty = false;
            }
            GUILayout.EndHorizontal();
        }
    }
}