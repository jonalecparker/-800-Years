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
    private Image curveButtonImage;
    private Image offsetButtonImage;
    private GameObject dragCountPanel;
    private Text dragCountText;
    private GameObject heightPanel;
    private Text heightText;

    void Start()
    {
        if (categories == null || categories.Length == 0)
            categories = DefaultCategories();

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
                items = new[] { new MenuItem { itemName = "Wall Segment", prefab = wallPrefab } }
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

        // Curved toggles the build tool between straight runs and the
        // three-click curve flow; it stays lit while the curve shape is
        // armed. Offset arms the parallel-wall tool (click a wall, scroll
        // the distance, click to lay a parallel copy). Delete sits
        // beside them: one click arms the delete tool (button turns
        // red), a second click — or picking any build item — returns to
        // building.
        curveButtonImage = CreateButton(categoryRow, "Curved", ToggleCurveShape).GetComponent<Image>();
        offsetButtonImage = CreateButton(categoryRow, "Offset", ToggleOffsetMode).GetComponent<Image>();
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
        // offsetting), so the readout follows the tool.
        bool building = placementSystem.Mode != GridPlacementSystem.ToolMode.Delete;
        heightPanel.SetActive(building);
        if (building)
            heightText.text = $"Height ×{placementSystem.HeightMultiplier:0.##}  ({placementSystem.CurrentWallHeight:0.##}m)";
    }

    void ToggleCurveShape()
    {
        if (placementSystem == null)
            return;

        // Arming a build shape always puts the build tool back in hand.
        if (placementSystem.Mode != GridPlacementSystem.ToolMode.Build)
        {
            placementSystem.SetMode(GridPlacementSystem.ToolMode.Build);
            deleteButtonImage.color = buttonColor;
            offsetButtonImage.color = buttonColor;
        }

        bool entering = placementSystem.Shape == GridPlacementSystem.BuildShape.Straight;
        placementSystem.SetShape(entering
            ? GridPlacementSystem.BuildShape.Curved
            : GridPlacementSystem.BuildShape.Straight);
        curveButtonImage.color = entering ? selectedColor : buttonColor;
    }

    void ToggleOffsetMode()
    {
        if (placementSystem == null)
            return;

        bool entering = placementSystem.Mode != GridPlacementSystem.ToolMode.Offset;
        placementSystem.SetMode(entering ? GridPlacementSystem.ToolMode.Offset : GridPlacementSystem.ToolMode.Build);
        offsetButtonImage.color = entering ? selectedColor : buttonColor;
        deleteButtonImage.color = buttonColor;

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
        deleteButtonImage.color = entering ? deleteActiveColor : buttonColor;
        offsetButtonImage.color = buttonColor;

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

            // Picking something to build always disarms the other tools.
            placementSystem.SetMode(GridPlacementSystem.ToolMode.Build);
            deleteButtonImage.color = buttonColor;
            offsetButtonImage.color = buttonColor;
        }

        itemRow.gameObject.SetActive(false);
        openCategory = null;
    }
}
