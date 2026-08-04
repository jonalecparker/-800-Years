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
    private Image doorButtonImage;
    // The Room tool's job buttons, shown in the item row while it's in
    // hand — tinted from the placement system's actual state like every
    // other control here.
    private readonly List<(Image image, GridPlacementSystem.RoomAction task)> roomTaskButtons =
        new List<(Image, GridPlacementSystem.RoomAction)>();
    private Toggle curvedToggle;
    private Toggle offsetToggle;
    private Toggle circleToggle;
    private Toggle rectToggle;
    private GameObject dragCountPanel;
    private Text dragCountText;
    private GameObject heightPanel;
    private Text heightText;
    private GameObject logBody;
    private Text logText;
    private Text logHeaderText;
    private bool logCollapsed;
    private int logVersionSeen = -1;
    private Text hintHeaderText;
    private Text hintText;
    private string hintContextSeen;

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
                // The bottom of the wall-type ladder. One type exists so
                // far; stronger walls join this list as they're designed.
                items = new[] { new MenuItem { itemName = "Palisade", prefab = wallPrefab } }
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
        // Room tool, and Delete (one click arms it — button turns red — a
        // second click or picking any build item returns to building).
        // Gesture helpers (Curved, Offset, Circle, Rectangle) live as
        // checkboxes in the options panel instead.
        roomButtonImage = CreateButton(categoryRow, "Room", ToggleRoomMode).GetComponent<Image>();
        stairButtonImage = CreateButton(categoryRow, "Stairs", ToggleStairMode).GetComponent<Image>();
        doorButtonImage = CreateButton(categoryRow, "Doors", ToggleDoorMode).GetComponent<Image>();
        deleteButtonImage = CreateButton(categoryRow, "Delete", ToggleDeleteMode).GetComponent<Image>();

        // Small readouts tucked above the bar's right end: the wall height
        // for the build tool (always visible while building), and the piece
        // count (only while a drag is running).
        dragCountPanel = CreateReadout(canvasObj.transform, "DragCountPanel",
            new Vector2(-8f, 68f), new Vector2(120f, 40f), out dragCountText);
        dragCountPanel.SetActive(false);
        heightPanel = CreateReadout(canvasObj.transform, "HeightPanel",
            new Vector2(-136f, 68f), new Vector2(190f, 40f), out heightText);
        heightPanel.SetActive(false);

        CreateOptionsPanel(canvasObj.transform);
        CreateLogPanel(canvasObj.transform);
        CreateHintPanel(canvasObj.transform);
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
    // walls AND rooms), then session options like the top lock. The
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
        if (placementSystem == null)
            return;

        int count = placementSystem.ActiveDragCount;
        if (count > 0)
            dragCountText.text = count == 1 ? "1 wall" : count + " walls";
        dragCountPanel.SetActive(count > 0);

        // Height only means anything while placing walls (building or
        // offsetting), so the readout follows the tool. A stair takes its
        // climb from the wall it's against and a doorway takes its head
        // from a constant, so the dial says nothing in either.
        bool building = placementSystem.Mode != GridPlacementSystem.ToolMode.Delete
            && placementSystem.Mode != GridPlacementSystem.ToolMode.Stair
            && placementSystem.Mode != GridPlacementSystem.ToolMode.Door;
        heightPanel.SetActive(building);
        if (building)
        {
            // EffectiveWallHeight, not CurrentWallHeight: while snapped to
            // a wall the ghost adopts that wall's height, and the readout
            // shows what would actually be built.
            float height = placementSystem.EffectiveWallHeight;
            heightText.text = $"Height ×{height / placementSystem.BaseHeight:0.##}  ({height:0.##}m)";
        }

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
        roomButtonImage.color = placementSystem.Mode == GridPlacementSystem.ToolMode.Room
            ? selectedColor : buttonColor;
        bool roomTool = placementSystem.Mode == GridPlacementSystem.ToolMode.Room;
        foreach ((Image image, GridPlacementSystem.RoomAction task) in roomTaskButtons)
            if (image != null)
                image.color = roomTool && placementSystem.RoomTask == task
                    ? selectedColor : buttonColor;
        stairButtonImage.color = placementSystem.Mode == GridPlacementSystem.ToolMode.Stair
            ? selectedColor : buttonColor;
        doorButtonImage.color = placementSystem.Mode == GridPlacementSystem.ToolMode.Door
            ? selectedColor : buttonColor;
        deleteButtonImage.color = placementSystem.Mode == GridPlacementSystem.ToolMode.Delete
            ? deleteActiveColor : buttonColor;
    }

    // The Room tool: chained wall placement that closes into a designated
    // room (floor now, roof when the walls crown level). The active shape
    // helper applies — Circle or Rectangle draws the whole room in one
    // gesture. Its two jobs open in the item row like a category's
    // contents: Build (the default) and Designate.
    void ToggleRoomMode()
    {
        if (placementSystem == null)
            return;

        bool entering = placementSystem.Mode != GridPlacementSystem.ToolMode.Room;
        placementSystem.SetMode(entering ? GridPlacementSystem.ToolMode.Room : GridPlacementSystem.ToolMode.Build);
        if (entering)
            ShowRoomTasks();
        else
        {
            itemRow.gameObject.SetActive(false);
            openCategory = null;
        }
    }

    // Build chains walls into a new room; Designate hands an EXISTING
    // enclosure a floor and roof, and only runs while it's picked.
    void ShowRoomTasks()
    {
        openCategory = null;
        roomTaskButtons.Clear();
        foreach (Transform child in itemRow)
            Destroy(child.gameObject);
        AddRoomTaskButton("Build", GridPlacementSystem.RoomAction.Build);
        AddRoomTaskButton("Designate", GridPlacementSystem.RoomAction.Designate);
        itemRow.gameObject.SetActive(true);
    }

    void AddRoomTaskButton(string label, GridPlacementSystem.RoomAction task)
    {
        GameObject button = CreateButton(itemRow, label, () =>
        {
            if (placementSystem != null)
                placementSystem.SetRoomTask(task);
        });
        roomTaskButtons.Add((button.GetComponent<Image>(), task));
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

    // Doors: same shape of tool as stairs — no gesture, no sub-jobs.
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
        roomTaskButtons.Clear();
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

            // Picking something to build always disarms the other tools
            // (the bar and checkboxes re-sync from state in Update).
            placementSystem.SetMode(GridPlacementSystem.ToolMode.Build);
        }

        itemRow.gameObject.SetActive(false);
        openCategory = null;
    }
}
