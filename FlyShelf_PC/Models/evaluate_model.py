import os
import sys
import numpy as np
import cv2
import onnxruntime as ort
from datasets import load_dataset
from tqdm import tqdm
from PIL import Image

def calculate_iou(box1, box2):
    # box format: [x, y, w, h]
    x1_1, y1_1, w1, h1 = box1
    x1_2, y1_2 = x1_1 + w1, y1_1 + h1
    
    x2_1, y2_1, w2, h2 = box2
    x2_2, y2_2 = x2_1 + w2, y2_1 + h2
    
    xi_1 = max(x1_1, x2_1)
    yi_1 = max(y1_1, y2_1)
    xi_2 = min(x1_2, x2_2)
    yi_2 = min(y1_2, y2_2)
    
    iw = max(0, xi_2 - xi_1)
    ih = max(0, yi_2 - yi_1)
    inter_area = iw * ih
    
    union_area = (w1 * h1) + (w2 * h2) - inter_area
    if union_area <= 0:
        return 0.0
    return inter_area / union_area

def apply_nms(boxes, iou_threshold=0.45):
    if not boxes:
        return []
    # boxes format: list of dicts: {"box": [x, y, w, h], "conf": conf, "class": class_id}
    sorted_boxes = sorted(boxes, key=lambda x: x["conf"], reverse=True)
    kept = []
    active = [True] * len(sorted_boxes)
    
    for i in range(len(sorted_boxes)):
        if not active[i]:
            continue
        kept.append(sorted_boxes[i])
        for j in range(i + 1, len(sorted_boxes)):
            if not active[j]:
                continue
            iou = calculate_iou(sorted_boxes[i]["box"], sorted_boxes[j]["box"])
            if iou > iou_threshold:
                active[j] = False
    return kept

def preprocess_image(pil_img):
    orig_w, orig_h = pil_img.size
    scale = min(640.0 / orig_w, 640.0 / orig_h)
    scaled_w = int(round(orig_w * scale))
    scaled_h = int(round(orig_h * scale))
    pad_x = (640 - scaled_w) // 2
    pad_y = (640 - scaled_h) // 2
    
    # Convert PIL Image to RGB numpy array
    img_rgb = np.array(pil_img.convert("RGB"))
    # Resize image
    img_resized = cv2.resize(img_rgb, (scaled_w, scaled_h), interpolation=cv2.INTER_LINEAR)
    
    # Create letterbox canvas filled with 114 (YOLOv8 gray padding)
    canvas = np.ones((640, 640, 3), dtype=np.uint8) * 114
    canvas[pad_y:pad_y+scaled_h, pad_x:pad_x+scaled_w, :] = img_resized
    
    # Normalize to [0, 1] range
    tensor_data = canvas.astype(np.float32) / 255.0
    # Convert to NCHW format
    tensor_data = np.transpose(tensor_data, (2, 0, 1))
    tensor_data = np.expand_dims(tensor_data, axis=0)
    
    return tensor_data, scale, pad_x, pad_y

def run_evaluation():
    model_path = os.path.join(os.path.dirname(__file__), "table_detect.onnx")
    if not os.path.exists(model_path):
        print(f"ERROR: Model not found at {model_path}")
        sys.exit(1)
        
    print(f"Loading ONNX Model from: {model_path}")
    session = ort.InferenceSession(model_path)
    
    input_name = session.get_inputs()[0].name
    output_name = session.get_outputs()[0].name
    print(f"Model Inputs: {[x.name for x in session.get_inputs()]}")
    print(f"Model Outputs: {[x.name for x in session.get_outputs()]}")
    
    print("Loading Hugging Face Dataset: keremberke/table-extraction (full)...")
    dataset = load_dataset("keremberke/table-extraction", revision="refs/convert/parquet")
    
    for split in ["test", "validation"]:
        if split not in dataset:
            continue
        
        print(f"\nEvaluating Split: {split.upper()} ({len(dataset[split])} images)")
        
        total_gt = 0
        total_pred = 0
        true_positives = 0
        ious = []
        
        conf_threshold = 0.40
        iou_threshold = 0.40
        
        for row in tqdm(dataset[split]):
            pil_img = row["image"]
            gt_objects = row["objects"]
            
            # Ground truth boxes [x, y, w, h] and classes
            gt_boxes = gt_objects["bbox"]
            gt_classes = gt_objects["category"]
            total_gt += len(gt_boxes)
            
            # Preprocess image
            tensor_data, scale, pad_x, pad_y = preprocess_image(pil_img)
            
            # Run model inference
            outputs = session.run([output_name], {input_name: tensor_data})
            output_tensor = outputs[0]  # Shape: [1, features, 8400]
            
            features = output_tensor.shape[1]
            num_boxes = output_tensor.shape[2]
            
            candidates = []
            for col in range(num_boxes):
                max_conf = 0.0
                best_class = 0
                
                # Class scores start at index 4
                for c in range(4, features):
                    score = output_tensor[0, c, col]
                    if score > max_conf:
                        max_conf = score
                        best_class = c - 4
                        
                if max_conf > conf_threshold:
                    cx = output_tensor[0, 0, col]
                    cy = output_tensor[0, 1, col]
                    w = output_tensor[0, 2, col]
                    h = output_tensor[0, 3, col]
                    
                    left = cx - w / 2.0
                    top = cy - h / 2.0
                    
                    # Scale back to original coordinates
                    orig_x = (left - pad_x) / scale
                    orig_y = (top - pad_y) / scale
                    orig_w = w / scale
                    orig_h = h / scale
                    
                    candidates.append({
                        "box": [orig_x, orig_y, orig_w, orig_h],
                        "conf": float(max_conf),
                        "class": best_class
                    })
            
            # Apply NMS
            predictions = apply_nms(candidates, iou_threshold=0.45)
            # Only count predicted tables (class 0 or 1)
            predictions = [p for p in predictions if p["class"] in [0, 1]]
            total_pred += len(predictions)
            
            # Match predictions to ground truth boxes
            matched_gt = set()
            for pred in predictions:
                best_iou = 0.0
                best_gt_idx = -1
                
                for idx, gt_box in enumerate(gt_boxes):
                    if idx in matched_gt:
                        continue
                    iou = calculate_iou(pred["box"], gt_box)
                    if iou > best_iou:
                        best_iou = iou
                        best_gt_idx = idx
                
                if best_iou >= iou_threshold and best_gt_idx != -1:
                    true_positives += 1
                    matched_gt.add(best_gt_idx)
                    ious.append(best_iou)
                    
        # Compute metrics
        precision = true_positives / total_pred if total_pred > 0 else 0.0
        recall = true_positives / total_gt if total_gt > 0 else 0.0
        f1 = (2 * precision * recall) / (precision + recall) if (precision + recall) > 0 else 0.0
        mean_iou = np.mean(ious) if ious else 0.0
        
        print(f"\n--- Results for {split.upper()} split ---")
        print(f"Total Ground Truth Tables: {total_gt}")
        print(f"Total Predicted Tables:    {total_pred}")
        print(f"True Positives:            {true_positives}")
        print(f"Precision:                 {precision:.4f}")
        print(f"Recall:                    {recall:.4f}")
        print(f"F1-Score:                  {f1:.4f}")
        print(f"Mean IoU (matched boxes):  {mean_iou:.4f}")

if __name__ == "__main__":
    run_evaluation()
