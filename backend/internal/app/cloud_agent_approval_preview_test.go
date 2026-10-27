package app

import (
	"encoding/json"
	"errors"
	"strings"
	"testing"
)

func stringPtr(value string) *string { return &value }

func TestCloudAgentCanvasApprovalPreviewDescribesUpdateTargetAndFields(t *testing.T) {
	doc, err := creationDocument(`{"nodes":[{"id":"video-1","type":"video","title":"满月庆祝视频草稿","metadata":{"content":"已提交结果","prompt":"private prompt","status":"succeeded"}}],"connections":[]}`)
	if err != nil {
		t.Fatal(err)
	}
	newTitle := "满月庆祝视频草稿（舒缓呼吸感）"
	newPrompt := "private next prompt"
	items, err := applyCloudAgentCanvasPlan(doc, []agentCanvasOp{{
		Type: "update_node", ID: "video-1", Patch: map[string]any{"title": newTitle, "content": newPrompt},
	}})
	if err != nil {
		t.Fatal(err)
	}
	if len(items) != 1 {
		t.Fatalf("expected one preview item, got %d", len(items))
	}
	item := items[0]
	if item.NodeTitle != "满月庆祝视频草稿" || item.NodeType != "video" || item.NodeTypeLabel != "视频" {
		t.Fatalf("preview lost target identity: %+v", item)
	}
	if item.ResultTitle != newTitle || strings.Join(item.Fields, "、") != "节点名称、下一版提示词" {
		t.Fatalf("preview lost changed fields: %+v", item)
	}
	raw, _ := json.Marshal(item)
	if strings.Contains(string(raw), "private") {
		t.Fatalf("preview exposed private content or internal id: %s", raw)
	}
	metadata := doc["nodes"].([]map[string]any)[0]["metadata"].(map[string]any)
	if metadata["content"] != "已提交结果" || metadata["prompt"] != "private prompt" || metadata["composerContent"] != newPrompt {
		t.Fatalf("preview mutated media result incorrectly: %+v", metadata)
	}
}

func TestCloudAgentCanvasApprovalPreviewDescribesConnectionsAndMixedOperations(t *testing.T) {
	doc, err := creationDocument(`{"nodes":[{"id":"image-1","type":"image","title":"参考图"},{"id":"video-1","type":"video","title":"视频镜头"}],"connections":[]}`)
	if err != nil {
		t.Fatal(err)
	}
	items, err := applyCloudAgentCanvasPlan(doc, []agentCanvasOp{
		{Type: "add_node", ID: "text-1", NodeType: "text", Title: stringPtr("镜头备注"), Content: stringPtr("private body")},
		{Type: "update_node", ID: "video-1", Patch: map[string]any{"title": "视频镜头新版"}},
		{Type: "connect_nodes", ID: "edge-1", FromNodeID: "image-1", ToNodeID: "video-1"},
	})
	if err != nil {
		t.Fatal(err)
	}
	if len(items) != 3 {
		t.Fatalf("expected mixed preview items, got %d", len(items))
	}
	if !strings.Contains(items[0].Summary, "新增文本") || !strings.Contains(items[1].Summary, "节点名称") {
		t.Fatalf("mixed preview summaries are incomplete: %+v", items)
	}
	if !strings.Contains(items[2].Summary, "参考图") || !strings.Contains(items[2].Summary, "视频镜头") {
		t.Fatalf("connection preview omitted endpoints: %+v", items[2])
	}
	preview := cloudAgentCanvasApprovalPreview(items)
	if !strings.Contains(preview.Description, "新增 1 个节点") || !strings.Contains(preview.Description, "修改 1 个节点") || !strings.Contains(preview.Description, "建立 1 条引用连线") {
		t.Fatalf("preview description omitted operation counts: %s", preview.Description)
	}
}

func TestCloudAgentCanvasApprovalPreviewRejectsUnknownTargetInsteadOfFallingBack(t *testing.T) {
	doc, err := creationDocument(`{"nodes":[],"connections":[]}`)
	if err != nil {
		t.Fatal(err)
	}
	_, err = applyCloudAgentCanvasPlan(doc, []agentCanvasOp{{
		Type: "update_node", ID: "missing", Patch: map[string]any{"title": "不能静默写入"},
	}})
	if err == nil || !strings.Contains(err.Error(), "只能更新现有") {
		t.Fatalf("unknown target was not rejected: %v", err)
	}
}

// 基于图片产出文本时，文本节点既是结果又是下一版提示词：正文必须同时写入
// content 与 prompt/composerContent，否则画布输入框会是空的。
func TestCloudAgentCanvasApprovalPreviewKeepsTextPromptAndAllowsImageReference(t *testing.T) {
	doc, err := creationDocument(`{"nodes":[{"id":"image-1","type":"image","title":"红色上衣"}],"connections":[]}`)
	if err != nil {
		t.Fatal(err)
	}
	items, err := applyCloudAgentCanvasPlan(doc, []agentCanvasOp{
		{Type: "add_node", ID: "text-1", NodeType: "text", Title: stringPtr("红色上衣 · 卖点"), Content: stringPtr("卖点正文")},
		{Type: "connect_nodes", ID: "edge-1", FromNodeID: "image-1", ToNodeID: "text-1"},
	})
	if err != nil {
		t.Fatalf("图片到文本节点的参考连线必须被接受：%v", err)
	}
	if len(items) != 2 {
		t.Fatalf("expected add and connect preview items, got %d", len(items))
	}
	metadata := doc["nodes"].([]map[string]any)[1]["metadata"].(map[string]any)
	if metadata["content"] != "卖点正文" || metadata["prompt"] != "卖点正文" || metadata["composerContent"] != "卖点正文" {
		t.Fatalf("text node lost prompt draft: %+v", metadata)
	}
}

// 漏 patch 必须是**可恢复的参数错误**：运行期会把它当工具结果回给模型重试，
// 而不是把整轮判死；未知操作类型（例如删除）仍按准入失败终止。
func TestCloudAgentCanvasApprovalPreviewTreatsMissingPatchAsArgumentError(t *testing.T) {
	doc, err := creationDocument(`{"nodes":[{"id":"image-1","type":"image","title":"参考图"}],"connections":[]}`)
	if err != nil {
		t.Fatal(err)
	}
	_, err = applyCloudAgentCanvasPlan(doc, []agentCanvasOp{{Type: "update_node", ID: "image-1"}})
	var argumentErr *cloudAgentArgumentError
	if !errors.As(err, &argumentErr) {
		t.Fatalf("漏 patch 应当是可恢复的参数错误，实际：%v", err)
	}

	_, err = applyCloudAgentCanvasPlan(doc, []agentCanvasOp{{Type: "delete_node", ID: "image-1"}})
	if err == nil {
		t.Fatal("未知画布写操作必须仍然报错")
	}
	if errors.As(err, &argumentErr) {
		t.Fatalf("未知画布写操作不能被当成可恢复的参数错误：%v", err)
	}
}
