using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

public class BuildMenu : MonoBehaviour
{
    [System.Serializable]
    public class MenuItem
    {
        public string itemName;
        public GameObject prefab;
        // What kind of wall this item builds — picking the item hands the
        // kind to the placement system (material + price, never geometry).
        public WallKind kind;
    }

    [System.Serializable]
    public class MenuCategory
    {
        public string categoryName;
        public MenuItem[] items;
    }

    public GridPlacementSystem placementSystem;
    public MenuCategory[] categories;

    public Color barColor = new Color(0.1f, 0.1f, 0.1f, 0.85f);
    public Color buttonColor = new Color(0.25f, 0.25f, 0.25f, 1f);
    public Color selectedColor = new Color(0.3f, 0.55f, 0.9f, 1f);
    public Color deleteActiveColor = new Color(0.75f, 0.22f, 0.18f, 1f);

    private RectTransform itemRow;
    private string openCategory;
    private Image deleteButtonImage;
    private Image roomButtonImage;
    private Image stairButtonImage;
    private Image bridgeButtonImage;
    private Image doorButtonImage;
    private Image buttressButtonImage;
    private Image crenelButtonImage;
    private Image groundButtonImage;
    // Sub-tool buttons shown in the item row while their category is in
    // hand — tinted from the placement system's actual state like every
    // other control here. Room's jobs are the building tools in build
    // order: Foundation, then Wall (the chain), then Floor.
    private readonly List<(Image image, GridPlacementSystem.GroundAction task)> groundTaskButtons =
        new List<(Image, GridPlacementSystem.GroundAction)>();
    private readonly List<(Image image, GridPlacementSystem.ToolMode mode)> roomTaskButtons =
        new List<(Image, GridPlacementSystem.ToolMode)>();
    // The Saves panel: a modal list of named save slots. While it's open
    // it owns the keyboard (slot names are typed), so the placement
    // system and walk mode both check this flag and stand aside.
    public static bool ModalOpen { get; private set; }
    private Image savesButtonImage;
    private GameObject savesPanel;
    private Text saveNameText;
    private RectTransform savesListParent;
    private string saveName = "";
    // The panel has two MODES, because one list where a click means load
    // is a list where saving over a slot is the one thing you cannot do
    // (the user kept loading the save they meant to overwrite). Save is
    // the mode the panel opens in — that is what the button is for — and
    // overwriting an existing slot asks once, since it destroys a save.
    private bool savesLoadMode;
    private string overwriteArmed = "";
    private Image saveTabImage;
    private Image loadTabImage;
    private Text savesTitle;
    private GameObject saveNameRow;
    private Toggle curvedToggle;
    private Toggle offsetToggle;
    private Toggle circleToggle;
    private Toggle rectToggle;
    private GameObject dragCountPanel;
    private Text dragCountText;
    private GameObject heightPanel;
    private Text heightText;
    private GameObject inspectPanel;
    private Text inspectText;
    private Text treasuryText;
    private Image surveyButtonImage;
    private Text clockText;
    private Text speedText;
    private GameObject logBody;
    private Text logText;
    private Text logHeaderText;
    private bool logCollapsed;
    private int logVersionSeen = -1;
    private Text hintHeaderText;
    private Text hintText;
    private string hintContextSeen;
    // The whole build HUD, switched off while walking.
    private GameObject hudCanvas;

    const int LogLinesShown = 6;

    void Start()
    {
        if (categories == null || categories.Length == 0)
            categories = DefaultCategories();

        BuildLog.Clear();
        EnsureEventSystem();
        BuildUI();
    }

    // Placeholder roster until real object types exist — keeps the menu
    // usable out of the box without requiring Inspector wiring for the only
    // piece that currently exists.
    MenuCategory[] DefaultCategories()
    {
        GameObject wallPrefab = placementSystem != null ? placementSystem.piecePrefab : null;
        return new[]
        {
            new MenuCategory
            {
                categoryName = "Walls",
                // The wall-type cost ladder, cheap rung first: palisade
                // timber at a fraction of stone's price (Estate rates).
                // Fortified joins the list when it's designed.
                items = new[]
                {
                    new MenuItem { itemName = "Palisade", prefab = wallPrefab,
                        kind = WallKind.Palisade },
                    new MenuItem { itemName = "Stone", prefab = wallPrefab,
                        kind = WallKind.Stone },
                }
            }
        };
    }

    void EnsureEventSystem()
    {
        if (FindFirstObjectByType<EventSystem>() != null)
            return;

        GameObject es = new GameObject("EventSystem");
        es.AddComponent<EventSystem>();
        es.AddComponent<InputSystemUIInputModule>();
    }

    void BuildUI()
    {
        GameObject canvasObj = new GameObject("BuildMenuCanvas");
        hudCanvas = canvasObj;
        canvasObj.transform.SetParent(transform, false);
        Canvas canvas = canvasObj.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        CanvasScaler scaler = canvasObj.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        canvasObj.AddComponent<GraphicRaycaster>();

        // Item row sits directly above the category bar; hidden until a
        // category is opened, matching a "nested categories" bottom bar.
        itemRow = CreateRow(canvasObj.transform, "ItemRow", 70f);
        itemRow.gameObject.SetActive(false);

        RectTransform categoryRow = CreateRow(canvasObj.transform, "CategoryRow", 0f);

        foreach (MenuCategory category in categories)
        {
            MenuCategory capturedCategory = category;
            CreateButton(categoryRow, capturedCategory.categoryName, () => ToggleCategory(capturedCategory));
        }

        // The bar holds objects and tools: build items by category, the
        // Room category (Foundation / Wall / Floor as its jobs), and
        // Delete (one click arms it — button turns red — a second click
        // or picking any build item returns to building). Gesture helpers
        // (Curved, Offset, Circle, Rectangle) live as checkboxes in the
        // options panel instead.
        roomButtonImage = CreateButton(categoryRow, "Room", ToggleRoomMode).GetComponent<Image>();
        stairButtonImage = CreateButton(categoryRow, "Stairs", ToggleStairMode).GetComponent<Image>();
        bridgeButtonImage = CreateButton(categoryRow, "Bridge", ToggleBridgeMode).GetComponent<Image>();
        doorButtonImage = CreateButton(categoryRow, "Doors", ToggleDoorMode).GetComponent<Image>();
        buttressButtonImage = CreateButton(categoryRow, "Buttress", ToggleButtressMode).GetComponent<Image>();
        crenelButtonImage = CreateButton(categoryRow, "Crenels", ToggleCrenelMode).GetComponent<Image>();
        groundButtonImage = CreateButton(categoryRow, "Ground", ToggleGroundMode).GetComponent<Image>();
        surveyButtonImage = CreateButton(categoryRow, "Survey", ToggleSurveyMode).GetComponent<Image>();
        deleteButtonImage = CreateButton(categoryRow, "Delete", ToggleDeleteMode).GetComponent<Image>();
        savesButtonImage = CreateButton(categoryRow, "Saves", ToggleSavesPanel).GetComponent<Image>();

        // Small readouts tucked above the bar's right end: the wall height
        // for the build tool (always visible while building), and the piece
        // count (only while a drag is running).
        dragCountPanel = CreateReadout(canvasObj.transform, "DragCountPanel",
            new Vector2(-8f, 68f), new Vector2(120f, 40f), out dragCountText);
        dragCountPanel.SetActive(false);
        heightPanel = CreateReadout(canvasObj.transform, "HeightPanel",
            new Vector2(-136f, 68f), new Vector2(290f, 40f), out heightText);
        heightPanel.SetActive(false);
        // The hover inspect line: absolute planes of whatever the cursor
        // is over (a room's floor/roof, a wall's top, the ground), so you
        // can read the elevation a new build has to match.
        inspectPanel = CreateReadout(canvasObj.transform, "InspectPanel",
            new Vector2(-8f, 112f), new Vector2(290f, 40f), out inspectText);
        inspectPanel.SetActive(false);
        // The treasury, always on while building: money is the estate's
        // one number, and every commit's log line ends with it.
        CreateReadout(canvasObj.transform, "TreasuryPanel",
            new Vector2(-8f, 156f), new Vector2(200f, 40f), out treasuryText);
        // The calendar above it: today's date, the next quarter day,
        // and the speed dial — the seasonal rhythm made visible.
        CreateClockPanel(canvasObj.transform);

        CreateOptionsPanel(canvasObj.transform);
        CreateLogPanel(canvasObj.transform);
        CreateHintPanel(canvasObj.transform);
        CreateSavesPanel(canvasObj.transform);
    }

    // The Saves panel: centered, modal, and built from the same cloth as
    // the rest of the HUD. Save/Load tabs, one name field (always focused
    // — it is the only text on screen), a Save button, and every slot on
    // disk newest first with its date. The date is the file's write time
    // and the name is the file's name — the panel stores nothing the
    // Saves folder doesn't already say.
    void CreateSavesPanel(Transform parent)
    {
        savesPanel = new GameObject("SavesPanel", typeof(RectTransform));
        savesPanel.transform.SetParent(parent, false);
        RectTransform rt = savesPanel.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(0f, 60f);
        rt.sizeDelta = new Vector2(480f, 0f);

        Image bg = savesPanel.AddComponent<Image>();
        bg.color = new Color(barColor.r, barColor.g, barColor.b, 0.96f);
        VerticalLayoutGroup layout = savesPanel.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(14, 14, 10, 12);
        layout.spacing = 8f;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        ContentSizeFitter fitter = savesPanel.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        // The mode tabs. Nothing below them changes but the meaning of a
        // click on a slot row, which is precisely the thing that was
        // ambiguous.
        GameObject tabRow = new GameObject("TabRow", typeof(RectTransform));
        tabRow.transform.SetParent(savesPanel.transform, false);
        tabRow.AddComponent<LayoutElement>().minHeight = 40f;
        HorizontalLayoutGroup tabLayout = tabRow.AddComponent<HorizontalLayoutGroup>();
        tabLayout.spacing = 8f;
        tabLayout.childForceExpandWidth = true;
        tabLayout.childForceExpandHeight = true;
        saveTabImage = CreateButton(tabRow.transform, "Save",
            () => SetSavesMode(false)).GetComponent<Image>();
        loadTabImage = CreateButton(tabRow.transform, "Load",
            () => SetSavesMode(true)).GetComponent<Image>();

        savesTitle = CreateHintLabel(savesPanel.transform, "Title", 18, Color.white);

        // The name row: the live field and the Save button side by side.
        GameObject nameRow = new GameObject("NameRow", typeof(RectTransform));
        saveNameRow = nameRow;
        nameRow.transform.SetParent(savesPanel.transform, false);
        nameRow.AddComponent<LayoutElement>().minHeight = 40f;
        HorizontalLayoutGroup nameLayout = nameRow.AddComponent<HorizontalLayoutGroup>();
        nameLayout.spacing = 8f;
        nameLayout.childForceExpandWidth = false;
        nameLayout.childForceExpandHeight = true;

        GameObject field = new GameObject("NameField", typeof(RectTransform));
        field.transform.SetParent(nameRow.transform, false);
        LayoutElement fieldLayout = field.AddComponent<LayoutElement>();
        fieldLayout.minWidth = 300f;
        fieldLayout.flexibleWidth = 1f;
        Image fieldBg = field.AddComponent<Image>();
        fieldBg.color = new Color(0f, 0f, 0f, 0.5f);
        GameObject fieldTextObj = new GameObject("Label", typeof(RectTransform));
        fieldTextObj.transform.SetParent(field.transform, false);
        RectTransform fieldTextRt = fieldTextObj.GetComponent<RectTransform>();
        fieldTextRt.anchorMin = Vector2.zero;
        fieldTextRt.anchorMax = Vector2.one;
        fieldTextRt.offsetMin = new Vector2(10f, 0f);
        fieldTextRt.offsetMax = new Vector2(-10f, 0f);
        saveNameText = fieldTextObj.AddComponent<Text>();
        saveNameText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        saveNameText.alignment = TextAnchor.MiddleLeft;
        saveNameText.color = Color.white;
        saveNameText.fontSize = 18;

        CreateButton(nameRow.transform, "Save", CommitSave);

        // The slot list rebuilds itself every time the panel opens or a
        // save is written.
        GameObject list = new GameObject("SlotList", typeof(RectTransform));
        list.transform.SetParent(savesPanel.transform, false);
        VerticalLayoutGroup listLayout = list.AddComponent<VerticalLayoutGroup>();
        listLayout.spacing = 4f;
        listLayout.childForceExpandWidth = true;
        listLayout.childForceExpandHeight = false;
        ContentSizeFitter listFitter = list.AddComponent<ContentSizeFitter>();
        listFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        savesListParent = list.GetComponent<RectTransform>();

        savesPanel.SetActive(false);
    }

    void ToggleSavesPanel()
    {
        if (ModalOpen)
        {
            CloseSavesPanel();
            return;
        }
        ModalOpen = true;
        // The field opens holding the current slot's name, so re-saving
        // the save you're working in is one click. Saving is also the mode
        // it opens in — loading is the deliberate detour.
        saveName = CastleSave.CurrentSlot;
        savesLoadMode = false;
        overwriteArmed = "";
        RefreshSavesPanel();
        savesPanel.SetActive(true);
        var keyboard = UnityEngine.InputSystem.Keyboard.current;
        if (keyboard != null)
            keyboard.onTextInput += OnSaveNameChar;
    }

    void CloseSavesPanel()
    {
        ModalOpen = false;
        if (savesPanel != null)
            savesPanel.SetActive(false);
        var keyboard = UnityEngine.InputSystem.Keyboard.current;
        if (keyboard != null)
            keyboard.onTextInput -= OnSaveNameChar;
    }

    // Leaving play mode mid-panel must not leave the modal flag set or
    // the keyboard hook attached for whoever runs next.
    void OnDisable()
    {
        if (ModalOpen)
            CloseSavesPanel();
    }

    // Slot names come in as raw typed characters; only filename-safe
    // ones stick (CastleSave.SanitizeSlot is the same filter at commit,
    // so what you see is what the file is called).
    void OnSaveNameChar(char c)
    {
        if (!ModalOpen || savesLoadMode)
            return;
        if (!char.IsLetterOrDigit(c) && c != ' ' && c != '-' && c != '_')
            return;
        if (saveName.Length >= 30)
            return;
        saveName += c;
        RefreshSaveNameText();
    }

    void CommitSave()
    {
        string slot = CastleSave.SanitizeSlot(saveName);
        if (slot.Length == 0)
        {
            BuildLog.Add("Name the save first.");
            return;
        }
        if (placementSystem != null)
            placementSystem.SaveSlot(slot);
        saveName = slot;
        overwriteArmed = "";
        RefreshSavesPanel();
    }

    void RefreshSaveNameText()
    {
        saveNameText.text = saveName + "▌";
    }

    void SetSavesMode(bool load)
    {
        savesLoadMode = load;
        overwriteArmed = "";
        RefreshSavesPanel();
    }

    // A click on a slot row means whatever the mode says, and overwriting
    // asks first: the row itself becomes the question, so there is no
    // second window to dismiss and no way to confirm something other than
    // what was clicked. Arming one row disarms any other.
    void ClickSlot(string slot)
    {
        if (savesLoadMode)
        {
            if (placementSystem != null)
                placementSystem.LoadSlot(slot);
            CloseSavesPanel();
            return;
        }
        if (overwriteArmed != slot)
        {
            overwriteArmed = slot;
            saveName = slot;
            RefreshSavesPanel();
            return;
        }
        overwriteArmed = "";
        saveName = slot;
        CommitSave();
    }

    void RefreshSavesPanel()
    {
        RefreshSaveNameText();
        if (savesTitle != null)
            savesTitle.text = savesLoadMode
                ? "Load — click a save to open it"
                : "Save — type a name and press Enter, or click a save to overwrite it";
        if (saveNameRow != null)
            saveNameRow.SetActive(!savesLoadMode);
        if (saveTabImage != null)
            saveTabImage.color = savesLoadMode ? buttonColor : selectedColor;
        if (loadTabImage != null)
            loadTabImage.color = savesLoadMode ? selectedColor : buttonColor;
        foreach (Transform child in savesListParent)
            Destroy(child.gameObject);
        foreach ((string slot, System.DateTime time) in CastleSave.ListSlots())
        {
            string capturedSlot = slot;
            bool armed = !savesLoadMode && overwriteArmed == slot;
            GameObject row = CreateButton(savesListParent,
                armed
                    ? $"Overwrite '{slot}'?    —    click again"
                    : $"{slot}    —    {time:yyyy-MM-dd HH:mm}",
                () => ClickSlot(capturedSlot));
            if (armed)
                row.GetComponent<Image>().color = deleteActiveColor;
            row.GetComponent<LayoutElement>().minHeight = 36f;
            Text label = row.GetComponentInChildren<Text>();
            label.alignment = TextAnchor.MiddleLeft;
            RectTransform labelRt = label.GetComponent<RectTransform>();
            labelRt.offsetMin = new Vector2(10f, 0f);
            labelRt.offsetMax = new Vector2(-44f, 0f);
            CreateSlotDeleteButton(row.transform, capturedSlot);
        }
    }

    // The trashcan on a slot row's right edge. Its own raycast target
    // sits on top of the row's, so the click deletes without loading.
    // Drawn from rects — the built-in font has no trashcan glyph.
    void CreateSlotDeleteButton(Transform row, string slot)
    {
        GameObject obj = new GameObject("DeleteButton", typeof(RectTransform));
        obj.transform.SetParent(row, false);
        RectTransform rt = obj.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(1f, 0.5f);
        rt.anchorMax = new Vector2(1f, 0.5f);
        rt.pivot = new Vector2(1f, 0.5f);
        rt.anchoredPosition = new Vector2(-6f, 0f);
        rt.sizeDelta = new Vector2(28f, 28f);
        Image bg = obj.AddComponent<Image>();
        bg.color = new Color(0.45f, 0.16f, 0.16f, 1f);
        Button button = obj.AddComponent<Button>();
        button.targetGraphic = bg;
        button.onClick.AddListener(() =>
        {
            CastleSave.DeleteSlot(slot);
            BuildLog.Add($"Deleted save '{slot}'.");
            overwriteArmed = "";
            RefreshSavesPanel();
        });

        void Part(string name, Vector2 pos, Vector2 size)
        {
            GameObject part = new GameObject(name, typeof(RectTransform));
            part.transform.SetParent(obj.transform, false);
            RectTransform prt = part.GetComponent<RectTransform>();
            prt.anchoredPosition = pos;
            prt.sizeDelta = size;
            Image img = part.AddComponent<Image>();
            img.color = new Color(0.95f, 0.9f, 0.9f, 1f);
            img.raycastTarget = false;
        }
        Part("Body", new Vector2(0f, -3f), new Vector2(12f, 12f));
        Part("Lid", new Vector2(0f, 5f), new Vector2(16f, 3f));
        Part("Handle", new Vector2(0f, 8.5f), new Vector2(6f, 2f));
    }

    // Top-right: what the keyboard means for the tool in hand, right now.
    // The placement system answers (ModifierHints) so the list lives
    // beside the input code it describes and can't drift from it.
    void CreateHintPanel(Transform parent)
    {
        GameObject panel = new GameObject("ModifierPanel", typeof(RectTransform));
        panel.transform.SetParent(parent, false);
        RectTransform rt = panel.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(1f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(1f, 1f);
        rt.anchoredPosition = new Vector2(-8f, -8f);
        rt.sizeDelta = new Vector2(340f, 0f);

        Image bg = panel.AddComponent<Image>();
        bg.color = barColor;
        VerticalLayoutGroup layout = panel.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(10, 10, 6, 8);
        layout.spacing = 4f;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        ContentSizeFitter fitter = panel.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        hintHeaderText = CreateHintLabel(panel.transform, "Header", 14,
            new Color(0.65f, 0.78f, 1f, 1f));
        hintText = CreateHintLabel(panel.transform, "Hints", 13,
            new Color(0.92f, 0.92f, 0.92f, 1f));
    }

    Text CreateHintLabel(Transform parent, string name, int fontSize, Color color)
    {
        GameObject obj = new GameObject(name, typeof(RectTransform));
        obj.transform.SetParent(parent, false);
        Text text = obj.AddComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.alignment = TextAnchor.UpperLeft;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.raycastTarget = false;
        text.color = color;
        text.fontSize = fontSize;
        text.text = "";
        obj.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        return text;
    }

    // The build log, top-left: a header that collapses the feed, and the
    // last few BuildLog entries newest-first — why a room came out
    // floor-only, what a gesture refusal meant, what a breach did.
    void CreateLogPanel(Transform parent)
    {
        GameObject panel = new GameObject("BuildLogPanel", typeof(RectTransform));
        panel.transform.SetParent(parent, false);
        RectTransform rt = panel.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(8f, -8f);
        rt.sizeDelta = new Vector2(460f, 0f);

        VerticalLayoutGroup layout = panel.AddComponent<VerticalLayoutGroup>();
        layout.spacing = 2f;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        ContentSizeFitter fitter = panel.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        GameObject header = new GameObject("LogHeader", typeof(RectTransform));
        header.transform.SetParent(panel.transform, false);
        header.AddComponent<LayoutElement>().minHeight = 24f;
        Image headerBg = header.AddComponent<Image>();
        headerBg.color = barColor;
        Button headerButton = header.AddComponent<Button>();
        headerButton.targetGraphic = headerBg;
        headerButton.onClick.AddListener(() =>
        {
            logCollapsed = !logCollapsed;
            logBody.SetActive(!logCollapsed);
            logHeaderText.text = logCollapsed ? "Build Log [+]" : "Build Log [-]";
        });
        GameObject headerTextObj = new GameObject("Label", typeof(RectTransform));
        headerTextObj.transform.SetParent(header.transform, false);
        RectTransform headerTextRt = headerTextObj.GetComponent<RectTransform>();
        headerTextRt.anchorMin = Vector2.zero;
        headerTextRt.anchorMax = Vector2.one;
        headerTextRt.offsetMin = new Vector2(8f, 0f);
        headerTextRt.offsetMax = Vector2.zero;
        logHeaderText = headerTextObj.AddComponent<Text>();
        logHeaderText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        logHeaderText.alignment = TextAnchor.MiddleLeft;
        logHeaderText.color = Color.white;
        logHeaderText.fontSize = 14;
        logHeaderText.text = "Build Log [-]";

        logBody = new GameObject("LogBody", typeof(RectTransform));
        logBody.transform.SetParent(panel.transform, false);
        Image bodyBg = logBody.AddComponent<Image>();
        bodyBg.color = new Color(barColor.r, barColor.g, barColor.b, barColor.a * 0.85f);
        VerticalLayoutGroup bodyLayout = logBody.AddComponent<VerticalLayoutGroup>();
        bodyLayout.padding = new RectOffset(8, 8, 6, 6);
        bodyLayout.childForceExpandWidth = true;
        bodyLayout.childForceExpandHeight = false;
        ContentSizeFitter bodyFitter = logBody.AddComponent<ContentSizeFitter>();
        bodyFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        GameObject textObj = new GameObject("Entries", typeof(RectTransform));
        textObj.transform.SetParent(logBody.transform, false);
        logText = textObj.AddComponent<Text>();
        logText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        logText.alignment = TextAnchor.UpperLeft;
        logText.color = new Color(0.92f, 0.92f, 0.92f, 1f);
        logText.fontSize = 13;
        logText.text = "";
    }

    // The helper panel on the left edge above the bar: gesture helpers
    // first (Curved bends the wall drag; Offset arms the parallel-wall
    // tool; Circle and Rectangle turn the drag into a whole figure, for
    // walls, rooms AND foundations), then session options like the top
    // lock. The
    // shape checkboxes are mutually exclusive — Update() keeps all of
    // them honest against the placement system's actual state.
    void CreateOptionsPanel(Transform parent)
    {
        GameObject panel = new GameObject("OptionsPanel", typeof(RectTransform));
        panel.transform.SetParent(parent, false);

        RectTransform rt = panel.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(0f, 0f);
        rt.pivot = new Vector2(0f, 0f);
        rt.anchoredPosition = new Vector2(8f, 140f);
        rt.sizeDelta = new Vector2(240f, 44f);

        Image bg = panel.AddComponent<Image>();
        bg.color = barColor;

        VerticalLayoutGroup layout = panel.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(10, 10, 8, 8);
        layout.spacing = 6f;
        layout.childAlignment = TextAnchor.UpperLeft;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        ContentSizeFitter fitter = panel.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        curvedToggle = CreateToggleRow(panel.transform, "Curved", false,
            on => SetShapeChecked(GridPlacementSystem.BuildShape.Curved, on));
        offsetToggle = CreateToggleRow(panel.transform, "Offset", false, on =>
        {
            if (placementSystem == null)
                return;
            placementSystem.SetMode(on
                ? GridPlacementSystem.ToolMode.Offset
                : GridPlacementSystem.ToolMode.Build);
            if (on)
            {
                itemRow.gameObject.SetActive(false);
                openCategory = null;
            }
        });
        circleToggle = CreateToggleRow(panel.transform, "Circle", false,
            on => SetShapeChecked(GridPlacementSystem.BuildShape.Circle, on));
        rectToggle = CreateToggleRow(panel.transform, "Rectangle", false,
            on => SetShapeChecked(GridPlacementSystem.BuildShape.Rect, on));
        CreateToggleRow(panel.transform, "Lock top height", false,
            on => { if (placementSystem != null) placementSystem.lockTopHeight = on; });
    }

    // Checking a shape arms it (leaving Delete/Offset for the build tool
    // if one of those was in hand — the shape rides Build and Room);
    // unchecking falls back to straight/freeform.
    void SetShapeChecked(GridPlacementSystem.BuildShape shape, bool on)
    {
        if (placementSystem == null)
            return;
        if (on)
        {
            if (placementSystem.Mode == GridPlacementSystem.ToolMode.Delete
                || placementSystem.Mode == GridPlacementSystem.ToolMode.Offset)
                placementSystem.SetMode(GridPlacementSystem.ToolMode.Build);
            placementSystem.SetShape(shape);
        }
        else if (placementSystem.Shape == shape)
        {
            placementSystem.SetShape(GridPlacementSystem.BuildShape.Straight);
        }
    }

    // A checkbox row: box + checkmark + label, clickable anywhere on the
    // row (the label Text is a raycast target too).
    Toggle CreateToggleRow(Transform parent, string label, bool initial, UnityEngine.Events.UnityAction<bool> onChanged)
    {
        GameObject row = new GameObject(label + "Toggle", typeof(RectTransform));
        row.transform.SetParent(parent, false);
        LayoutElement layoutElement = row.AddComponent<LayoutElement>();
        layoutElement.minHeight = 26f;

        GameObject box = new GameObject("Box", typeof(RectTransform));
        box.transform.SetParent(row.transform, false);
        RectTransform boxRt = box.GetComponent<RectTransform>();
        boxRt.anchorMin = new Vector2(0f, 0.5f);
        boxRt.anchorMax = new Vector2(0f, 0.5f);
        boxRt.pivot = new Vector2(0f, 0.5f);
        boxRt.anchoredPosition = Vector2.zero;
        boxRt.sizeDelta = new Vector2(22f, 22f);
        Image boxImage = box.AddComponent<Image>();
        boxImage.color = buttonColor;

        GameObject check = new GameObject("Check", typeof(RectTransform));
        check.transform.SetParent(box.transform, false);
        RectTransform checkRt = check.GetComponent<RectTransform>();
        checkRt.anchorMin = Vector2.zero;
        checkRt.anchorMax = Vector2.one;
        checkRt.offsetMin = new Vector2(4f, 4f);
        checkRt.offsetMax = new Vector2(-4f, -4f);
        Image checkImage = check.AddComponent<Image>();
        checkImage.color = selectedColor;

        GameObject textObj = new GameObject("Label", typeof(RectTransform));
        textObj.transform.SetParent(row.transform, false);
        RectTransform textRt = textObj.GetComponent<RectTransform>();
        textRt.anchorMin = Vector2.zero;
        textRt.anchorMax = Vector2.one;
        textRt.offsetMin = new Vector2(30f, 0f);
        textRt.offsetMax = Vector2.zero;
        Text text = textObj.AddComponent<Text>();
        text.text = label;
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.alignment = TextAnchor.MiddleLeft;
        text.color = Color.white;
        text.fontSize = 16;

        Toggle toggle = row.AddComponent<Toggle>();
        toggle.targetGraphic = boxImage;
        toggle.graphic = checkImage;
        toggle.isOn = initial;
        toggle.onValueChanged.AddListener(onChanged);
        return toggle;
    }

    // The calendar panel: date and countdown on the top line, the speed
    // slider below with its multiplier label. The slider maps its 0–1
    // travel exponentially onto 1×–120× so the low end has room — the
    // difference between 1× and 4× matters more than 80× vs 120×.
    void CreateClockPanel(Transform parent)
    {
        GameObject panel = new GameObject("ClockPanel", typeof(RectTransform));
        panel.transform.SetParent(parent, false);
        RectTransform rt = panel.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(1f, 0f);
        rt.anchorMax = new Vector2(1f, 0f);
        rt.pivot = new Vector2(1f, 0f);
        rt.anchoredPosition = new Vector2(-8f, 200f);
        rt.sizeDelta = new Vector2(300f, 64f);
        panel.AddComponent<Image>().color = barColor;

        clockText = CreatePanelText(panel.transform, "Date",
            new Vector2(10f, 34f), new Vector2(-10f, -4f), 16,
            TextAnchor.MiddleCenter);

        speedText = CreatePanelText(panel.transform, "Speed",
            new Vector2(-58f, 6f), new Vector2(-10f, 30f), 16,
            TextAnchor.MiddleRight);
        RectTransform st = speedText.rectTransform;
        st.anchorMin = new Vector2(1f, 0f);
        st.anchorMax = new Vector2(1f, 0f);
        st.offsetMin = new Vector2(-58f, 6f);
        st.offsetMax = new Vector2(-10f, 30f);

        Slider slider = CreateBareSlider(panel.transform,
            new Vector2(10f, 6f), new Vector2(-64f, 30f));
        slider.onValueChanged.AddListener(v =>
            GameClock.Speed = Mathf.Pow(GameClock.MaxSpeed, v));
    }

    Text CreatePanelText(Transform parent, string name, Vector2 offMin,
        Vector2 offMax, int size, TextAnchor align)
    {
        GameObject obj = new GameObject(name, typeof(RectTransform));
        obj.transform.SetParent(parent, false);
        RectTransform rt = obj.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = offMin;
        rt.offsetMax = offMax;
        Text text = obj.AddComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.alignment = align;
        text.color = Color.white;
        text.fontSize = size;
        return text;
    }

    // A Slider from raw parts — the HUD is code-built cloth, and Unity
    // ships no runtime default. Track, fill and handle only.
    Slider CreateBareSlider(Transform parent, Vector2 offMin, Vector2 offMax)
    {
        GameObject obj = new GameObject("SpeedSlider", typeof(RectTransform));
        obj.transform.SetParent(parent, false);
        RectTransform rt = obj.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = offMin;
        rt.offsetMax = offMax;

        GameObject track = new GameObject("Track", typeof(RectTransform));
        track.transform.SetParent(obj.transform, false);
        RectTransform trackRt = track.GetComponent<RectTransform>();
        trackRt.anchorMin = new Vector2(0f, 0.35f);
        trackRt.anchorMax = new Vector2(1f, 0.65f);
        trackRt.offsetMin = Vector2.zero;
        trackRt.offsetMax = Vector2.zero;
        track.AddComponent<Image>().color = buttonColor;

        GameObject fillArea = new GameObject("FillArea", typeof(RectTransform));
        fillArea.transform.SetParent(obj.transform, false);
        RectTransform faRt = fillArea.GetComponent<RectTransform>();
        faRt.anchorMin = new Vector2(0f, 0.35f);
        faRt.anchorMax = new Vector2(1f, 0.65f);
        faRt.offsetMin = new Vector2(0f, 0f);
        faRt.offsetMax = new Vector2(-8f, 0f);
        GameObject fill = new GameObject("Fill", typeof(RectTransform));
        fill.transform.SetParent(fillArea.transform, false);
        RectTransform fillRt = fill.GetComponent<RectTransform>();
        fillRt.sizeDelta = new Vector2(8f, 0f);
        fill.AddComponent<Image>().color = selectedColor;

        GameObject handleArea = new GameObject("HandleArea", typeof(RectTransform));
        handleArea.transform.SetParent(obj.transform, false);
        RectTransform haRt = handleArea.GetComponent<RectTransform>();
        haRt.anchorMin = Vector2.zero;
        haRt.anchorMax = Vector2.one;
        haRt.offsetMin = new Vector2(8f, 0f);
        haRt.offsetMax = new Vector2(-8f, 0f);
        GameObject handle = new GameObject("Handle", typeof(RectTransform));
        handle.transform.SetParent(handleArea.transform, false);
        RectTransform handleRt = handle.GetComponent<RectTransform>();
        handleRt.sizeDelta = new Vector2(16f, 0f);
        Image handleImage = handle.AddComponent<Image>();
        handleImage.color = Color.white;

        Slider slider = obj.AddComponent<Slider>();
        slider.fillRect = fillRt;
        slider.handleRect = handleRt;
        slider.targetGraphic = handleImage;
        slider.minValue = 0f;
        slider.maxValue = 1f;
        slider.value = 0f;
        return slider;
    }

    // Survey: the land tool — the parcel map, hover to read, click to
    // clear or buy frontier land. A world tool like Ground, so it lives
    // on the bar, not in a category.
    void ToggleSurveyMode()
    {
        if (placementSystem == null)
            return;

        bool entering = placementSystem.Mode != GridPlacementSystem.ToolMode.Land;
        placementSystem.SetMode(entering ? GridPlacementSystem.ToolMode.Land : GridPlacementSystem.ToolMode.Build);
        if (entering)
        {
            itemRow.gameObject.SetActive(false);
            openCategory = null;
        }
    }

    GameObject CreateReadout(Transform parent, string name, Vector2 anchoredPosition, Vector2 size, out Text text)
    {
        GameObject panel = new GameObject(name, typeof(RectTransform));
        panel.transform.SetParent(parent, false);

        RectTransform rt = panel.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(1f, 0f);
        rt.anchorMax = new Vector2(1f, 0f);
        rt.pivot = new Vector2(1f, 0f);
        rt.anchoredPosition = anchoredPosition;
        rt.sizeDelta = size;

        Image bg = panel.AddComponent<Image>();
        bg.color = barColor;

        GameObject textObj = new GameObject("Label", typeof(RectTransform));
        textObj.transform.SetParent(panel.transform, false);
        RectTransform textRt = textObj.GetComponent<RectTransform>();
        textRt.anchorMin = Vector2.zero;
        textRt.anchorMax = Vector2.one;
        textRt.offsetMin = Vector2.zero;
        textRt.offsetMax = Vector2.zero;

        text = textObj.AddComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.alignment = TextAnchor.MiddleCenter;
        text.color = Color.white;
        text.fontSize = 20;

        return panel;
    }

    void Update()
    {
        // The survey exists from the first frame (a no-op once made or
        // loaded), and the calendar runs through EVERYTHING — walk
        // mode, the Saves panel, all of it. Time passing while you walk
        // your lands is the point; standing down is deliberately not
        // done here.
        LandParcels.EnsureGenerated();
        VillageHouses.EnsureCurrent();
        LandPlot.Tick();
        GameClock.Tick(Time.deltaTime);

        // Walking has no tools, so it has no tool HUD. Switching the
        // canvas off takes its GraphicRaycaster with it, which is what
        // stops a locked cursor parked over a button from "hovering" one
        // it can no longer see.
        bool inBuildMode = !WalkMode.Active;
        if (hudCanvas != null && hudCanvas.activeSelf != inBuildMode)
            hudCanvas.SetActive(inBuildMode);
        if (!inBuildMode || placementSystem == null)
            return;

        // The Saves panel owns the keyboard while it's open: Backspace
        // edits the slot name, Enter commits the save. Characters arrive
        // through the onTextInput hook, not here.
        if (ModalOpen)
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            // Only the Save mode has a name to type or an Enter to mean
            // anything — in Load mode the keyboard would be editing a
            // field that isn't on screen.
            if (kb != null && !savesLoadMode)
            {
                if (kb.backspaceKey.wasPressedThisFrame && saveName.Length > 0)
                {
                    saveName = saveName.Substring(0, saveName.Length - 1);
                    RefreshSaveNameText();
                }
                if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame)
                    CommitSave();
            }
        }
        else
        {
            // Population test dial ([ / ], Shift ×5, the user's ask
            // 2026-08-13): households move, VillageHouses.EnsureCurrent
            // above rebuilds next frame, and the town visibly grows or
            // shrinks — houses are a prefix of a stable lot plan, so
            // growth only ever appends. A stand-in until the economy
            // grows households on its own; Households is saved (v6),
            // so a dialed population persists like any other fact.
            var popKb = UnityEngine.InputSystem.Keyboard.current;
            if (popKb != null)
            {
                int step = popKb.leftShiftKey.isPressed
                    || popKb.rightShiftKey.isPressed ? 5 : 1;
                int delta = (popKb.rightBracketKey.wasPressedThisFrame ? step : 0)
                    - (popKb.leftBracketKey.wasPressedThisFrame ? step : 0);
                if (delta != 0)
                {
                    Estate.Households = Mathf.Max(0, Estate.Households + delta);
                    BuildLog.Add($"The manor counts {Estate.Households}"
                        + $" household{(Estate.Households == 1 ? "" : "s")}.");
                }
            }
        }

        int count = placementSystem.ActiveDragCount;
        if (count > 0)
        {
            // The price rides the counter — what the click will cost,
            // priced from the same specs the ghosts show.
            long cost = placementSystem.ActiveDragCost;
            string walls = count == 1 ? "1 wall" : count + " walls";
            dragCountText.text = cost > 0
                ? walls + " · " + Estate.Format(cost) : walls;
        }
        dragCountPanel.SetActive(count > 0);

        // The treasury readout, red in debt — the masons build on credit.
        treasuryText.text = "Treasury " + Estate.Format(Estate.TreasuryPence);
        treasuryText.color = Estate.TreasuryPence < 0
            ? new Color(1f, 0.45f, 0.4f, 1f) : Color.white;

        // The calendar: today, the countdown to the next payday, and
        // what the dial is doing.
        clockText.text = GameClock.DateLine() + " · " + GameClock.NextQuarterLine();
        speedText.text = "×" + (GameClock.Speed < 10f
            ? GameClock.Speed.ToString("0.0") : GameClock.Speed.ToString("0"));

        // Height only means anything while placing walls (building or
        // offsetting), so the readout follows the tool. A stair takes its
        // climb from the wall it's against and a doorway takes its head
        // from a constant, so the dial says nothing in either.
        // The slab tools draw an outline at a stated plane, so their
        // readout speaks corners and the plane itself; the ghost-top
        // elevation is that plane.
        bool slabTool = placementSystem.Mode == GridPlacementSystem.ToolMode.Foundation
            || placementSystem.Mode == GridPlacementSystem.ToolMode.Floor;
        bool building = !slabTool
            && placementSystem.Mode != GridPlacementSystem.ToolMode.None
            && placementSystem.Mode != GridPlacementSystem.ToolMode.Delete
            && placementSystem.Mode != GridPlacementSystem.ToolMode.Stair
            && placementSystem.Mode != GridPlacementSystem.ToolMode.Bridge
            && placementSystem.Mode != GridPlacementSystem.ToolMode.Door
            && placementSystem.Mode != GridPlacementSystem.ToolMode.Buttress
            && placementSystem.Mode != GridPlacementSystem.ToolMode.Crenel;
        heightPanel.SetActive(building || slabTool);
        if (slabTool)
        {
            int corners = placementSystem.SlabChainCount;
            float? slabTop = placementSystem.GhostTopY;
            string label = corners > 0
                ? $"Outline · {corners} corner{(corners == 1 ? "" : "s")}"
                : "Outline";
            heightText.text = slabTop.HasValue
                ? $"{label}  plane {slabTop.Value:0.##}m" : label;
        }
        if (building)
        {
            // EffectiveWallHeight, not CurrentWallHeight: while snapped to
            // a wall the ghost adopts that wall's height, and the readout
            // shows what would actually be built. GhostTopY carries the
            // ABSOLUTE elevation the ghost's top lands at — the number a
            // rendezvous with another building's floor or roof dials
            // against.
            float height = placementSystem.EffectiveWallHeight;
            float? top = placementSystem.GhostTopY;
            heightText.text = top.HasValue
                ? $"Height ×{height / placementSystem.BaseHeight:0.##}  ({height:0.##}m)  top {top.Value:0.##}m"
                : $"Height ×{height / placementSystem.BaseHeight:0.##}  ({height:0.##}m)";
        }

        // What the cursor is over, as absolute planes — visible with the
        // same tools as the height readout, since matching is what both
        // numbers are for.
        string inspect = building || slabTool ? placementSystem.InspectLine : null;
        if (!string.IsNullOrEmpty(inspect))
            inspectText.text = inspect;
        inspectPanel.SetActive(!string.IsNullOrEmpty(inspect));

        // The hint panel follows the tool's phase, so it re-reads only
        // when the context line changes rather than every frame.
        placementSystem.ModifierHints(out string context, out string hints);
        if (context != hintContextSeen)
        {
            hintContextSeen = context;
            hintHeaderText.text = context;
            hintText.text = hints;
        }

        if (BuildLog.Version != logVersionSeen)
        {
            logVersionSeen = BuildLog.Version;
            int shownLines = Mathf.Min(LogLinesShown, BuildLog.Entries.Count);
            var lines = new System.Text.StringBuilder();
            for (int i = 0; i < shownLines; i++)
            {
                if (i > 0)
                    lines.Append('\n');
                lines.Append(BuildLog.Entries[BuildLog.Entries.Count - 1 - i]);
            }
            logText.text = lines.ToString();
            logBody.SetActive(!logCollapsed && BuildLog.Entries.Count > 0);
        }

        // The checkboxes and tool buttons mirror the placement system's
        // actual state rather than tracking their own — arming any tool
        // or shape by any route updates every control, and the shape
        // boxes stay mutually exclusive for free.
        curvedToggle.SetIsOnWithoutNotify(placementSystem.Shape == GridPlacementSystem.BuildShape.Curved);
        circleToggle.SetIsOnWithoutNotify(placementSystem.Shape == GridPlacementSystem.BuildShape.Circle);
        rectToggle.SetIsOnWithoutNotify(placementSystem.Shape == GridPlacementSystem.BuildShape.Rect);
        offsetToggle.SetIsOnWithoutNotify(placementSystem.Mode == GridPlacementSystem.ToolMode.Offset);
        bool roomFamily = placementSystem.Mode == GridPlacementSystem.ToolMode.Room
            || placementSystem.Mode == GridPlacementSystem.ToolMode.Foundation
            || placementSystem.Mode == GridPlacementSystem.ToolMode.Floor;
        roomButtonImage.color = roomFamily ? selectedColor : buttonColor;
        foreach ((Image image, GridPlacementSystem.ToolMode mode) in roomTaskButtons)
            if (image != null)
                image.color = placementSystem.Mode == mode ? selectedColor : buttonColor;
        groundButtonImage.color = placementSystem.Mode == GridPlacementSystem.ToolMode.Ground
            ? selectedColor : buttonColor;
        bool groundTool = placementSystem.Mode == GridPlacementSystem.ToolMode.Ground;
        foreach ((Image image, GridPlacementSystem.GroundAction task) in groundTaskButtons)
            if (image != null)
                image.color = groundTool && placementSystem.GroundTask == task
                    ? selectedColor : buttonColor;
        savesButtonImage.color = ModalOpen ? selectedColor : buttonColor;
        surveyButtonImage.color = placementSystem.Mode == GridPlacementSystem.ToolMode.Land
            ? selectedColor : buttonColor;
        stairButtonImage.color = placementSystem.Mode == GridPlacementSystem.ToolMode.Stair
            ? selectedColor : buttonColor;
        bridgeButtonImage.color = placementSystem.Mode == GridPlacementSystem.ToolMode.Bridge
            ? selectedColor : buttonColor;
        doorButtonImage.color = placementSystem.Mode == GridPlacementSystem.ToolMode.Door
            ? selectedColor : buttonColor;
        buttressButtonImage.color = placementSystem.Mode == GridPlacementSystem.ToolMode.Buttress
            ? selectedColor : buttonColor;
        crenelButtonImage.color = placementSystem.Mode == GridPlacementSystem.ToolMode.Crenel
            ? selectedColor : buttonColor;
        deleteButtonImage.color = placementSystem.Mode == GridPlacementSystem.ToolMode.Delete
            ? deleteActiveColor : buttonColor;
    }

    // Room: the building category. Its three jobs in the item row are the
    // building tools in build order — Foundation (the slab that sets the
    // level), Wall (the chain gesture that closes and announces an
    // enclosure; Circle/Rectangle ride it), Floor (the between-storey
    // tile). Opening the category arms Foundation, because a building
    // starts from one.
    void ToggleRoomMode()
    {
        if (placementSystem == null)
            return;

        bool inFamily = placementSystem.Mode == GridPlacementSystem.ToolMode.Room
            || placementSystem.Mode == GridPlacementSystem.ToolMode.Foundation
            || placementSystem.Mode == GridPlacementSystem.ToolMode.Floor;
        if (inFamily)
        {
            placementSystem.SetMode(GridPlacementSystem.ToolMode.Build);
            itemRow.gameObject.SetActive(false);
            openCategory = null;
        }
        else
        {
            placementSystem.SetMode(GridPlacementSystem.ToolMode.Foundation);
            ShowRoomTasks();
        }
    }

    void ShowRoomTasks()
    {
        openCategory = null;
        roomTaskButtons.Clear();
        foreach (Transform child in itemRow)
            Destroy(child.gameObject);
        AddRoomTaskButton("Foundation", GridPlacementSystem.ToolMode.Foundation);
        AddRoomTaskButton("Wall", GridPlacementSystem.ToolMode.Room);
        AddRoomTaskButton("Floor", GridPlacementSystem.ToolMode.Floor);
        itemRow.gameObject.SetActive(true);
    }

    void AddRoomTaskButton(string label, GridPlacementSystem.ToolMode mode)
    {
        GameObject button = CreateButton(itemRow, label, () =>
        {
            if (placementSystem != null)
                placementSystem.SetMode(mode);
        });
        roomTaskButtons.Add((button.GetComponent<Image>(), mode));
    }

    // Ground: earthworks under the deviation law. Two jobs in the item
    // row, like Room's: Path (bench a walkable band, the default) and
    // Restore (return ground to the natural hill).
    void ToggleGroundMode()
    {
        if (placementSystem == null)
            return;

        bool entering = placementSystem.Mode != GridPlacementSystem.ToolMode.Ground;
        placementSystem.SetMode(entering ? GridPlacementSystem.ToolMode.Ground : GridPlacementSystem.ToolMode.Build);
        if (entering)
            ShowGroundTasks();
        else
        {
            itemRow.gameObject.SetActive(false);
            openCategory = null;
        }
    }

    void ShowGroundTasks()
    {
        openCategory = null;
        groundTaskButtons.Clear();
        foreach (Transform child in itemRow)
            Destroy(child.gameObject);
        AddGroundTaskButton("Path", GridPlacementSystem.GroundAction.Path);
        AddGroundTaskButton("Restore", GridPlacementSystem.GroundAction.Restore);
        itemRow.gameObject.SetActive(true);
    }

    void AddGroundTaskButton(string label, GridPlacementSystem.GroundAction task)
    {
        GameObject button = CreateButton(itemRow, label, () =>
        {
            if (placementSystem != null)
                placementSystem.SetGroundTask(task);
        });
        groundTaskButtons.Add((button.GetComponent<Image>(), task));
    }

    // Stairs: no gesture and no sub-jobs, so the item row stays shut —
    // point at a wall's side face and click.
    void ToggleStairMode()
    {
        if (placementSystem == null)
            return;

        bool entering = placementSystem.Mode != GridPlacementSystem.ToolMode.Stair;
        placementSystem.SetMode(entering ? GridPlacementSystem.ToolMode.Stair : GridPlacementSystem.ToolMode.Build);
        if (entering)
        {
            itemRow.gameObject.SetActive(false);
            openCategory = null;
        }
    }

    // Bridge: the two-ends-then-width gesture; no sub-jobs.
    void ToggleBridgeMode()
    {
        if (placementSystem == null)
            return;

        bool entering = placementSystem.Mode != GridPlacementSystem.ToolMode.Bridge;
        placementSystem.SetMode(entering ? GridPlacementSystem.ToolMode.Bridge : GridPlacementSystem.ToolMode.Build);
        if (entering)
        {
            itemRow.gameObject.SetActive(false);
            openCategory = null;
        }
    }

    // Doors: same shape of tool as stairs — no gesture, no sub-jobs.
    // Buttresses: the same shape of tool again — point at a wall's face
    // and click. Its own bar button rather than a Room job, because a
    // buttress is added to a wall that already stands, like a doorway.
    void ToggleButtressMode()
    {
        if (placementSystem == null)
            return;

        bool entering = placementSystem.Mode != GridPlacementSystem.ToolMode.Buttress;
        placementSystem.SetMode(entering ? GridPlacementSystem.ToolMode.Buttress
            : GridPlacementSystem.ToolMode.Build);
        if (entering)
        {
            itemRow.gameObject.SetActive(false);
            openCategory = null;
        }
    }

    // Crenellations: added to a wall that already stands, exactly like a
    // doorway or a pier, so its own bar button rather than a Room job.
    void ToggleCrenelMode()
    {
        if (placementSystem == null)
            return;

        bool entering = placementSystem.Mode != GridPlacementSystem.ToolMode.Crenel;
        placementSystem.SetMode(entering ? GridPlacementSystem.ToolMode.Crenel
            : GridPlacementSystem.ToolMode.Build);
        if (entering)
        {
            itemRow.gameObject.SetActive(false);
            openCategory = null;
        }
    }

    void ToggleDoorMode()
    {
        if (placementSystem == null)
            return;

        bool entering = placementSystem.Mode != GridPlacementSystem.ToolMode.Door;
        placementSystem.SetMode(entering ? GridPlacementSystem.ToolMode.Door : GridPlacementSystem.ToolMode.Build);
        if (entering)
        {
            itemRow.gameObject.SetActive(false);
            openCategory = null;
        }
    }

    void ToggleDeleteMode()
    {
        if (placementSystem == null)
            return;

        bool entering = placementSystem.Mode != GridPlacementSystem.ToolMode.Delete;
        placementSystem.SetMode(entering ? GridPlacementSystem.ToolMode.Delete : GridPlacementSystem.ToolMode.Build);
        if (entering)
        {
            itemRow.gameObject.SetActive(false);
            openCategory = null;
        }
    }

    RectTransform CreateRow(Transform parent, string name, float bottomOffset)
    {
        GameObject row = new GameObject(name, typeof(RectTransform));
        row.transform.SetParent(parent, false);

        RectTransform rt = row.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(1f, 0f);
        rt.pivot = new Vector2(0.5f, 0f);
        rt.sizeDelta = new Vector2(0f, 60f);
        rt.anchoredPosition = new Vector2(0f, bottomOffset);

        Image bg = row.AddComponent<Image>();
        bg.color = barColor;

        HorizontalLayoutGroup layout = row.AddComponent<HorizontalLayoutGroup>();
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.spacing = 8f;
        layout.padding = new RectOffset(8, 8, 8, 8);
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = true;

        return rt;
    }

    GameObject CreateButton(Transform parent, string label, UnityEngine.Events.UnityAction onClick)
    {
        GameObject buttonObj = new GameObject(label + "Button", typeof(RectTransform));
        buttonObj.transform.SetParent(parent, false);

        LayoutElement layoutElement = buttonObj.AddComponent<LayoutElement>();
        layoutElement.minWidth = 140f;
        layoutElement.minHeight = 44f;

        Image bg = buttonObj.AddComponent<Image>();
        bg.color = buttonColor;

        Button button = buttonObj.AddComponent<Button>();
        button.targetGraphic = bg;
        button.onClick.AddListener(onClick);

        GameObject textObj = new GameObject("Label", typeof(RectTransform));
        textObj.transform.SetParent(buttonObj.transform, false);
        RectTransform textRt = textObj.GetComponent<RectTransform>();
        textRt.anchorMin = Vector2.zero;
        textRt.anchorMax = Vector2.one;
        textRt.offsetMin = Vector2.zero;
        textRt.offsetMax = Vector2.zero;

        Text text = textObj.AddComponent<Text>();
        text.text = label;
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.alignment = TextAnchor.MiddleCenter;
        text.color = Color.white;
        text.fontSize = 18;

        return buttonObj;
    }

    void ToggleCategory(MenuCategory category)
    {
        if (openCategory == category.categoryName)
        {
            itemRow.gameObject.SetActive(false);
            openCategory = null;
            return;
        }

        openCategory = category.categoryName;
        foreach (Transform child in itemRow)
            Destroy(child.gameObject);

        foreach (MenuItem item in category.items)
        {
            MenuItem capturedItem = item;
            CreateButton(itemRow, capturedItem.itemName, () => SelectItem(capturedItem));
        }

        itemRow.gameObject.SetActive(true);
    }

    void SelectItem(MenuItem item)
    {
        if (placementSystem != null)
        {
            if (item.prefab != null)
                placementSystem.piecePrefab = item.prefab;
            placementSystem.SetWallKind(item.kind);

            // Picking something to build always disarms the other tools
            // (the bar and checkboxes re-sync from state in Update).
            placementSystem.SetMode(GridPlacementSystem.ToolMode.Build);
        }

        itemRow.gameObject.SetActive(false);
        openCategory = null;
    }
}
