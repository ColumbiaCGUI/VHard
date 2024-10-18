import open3d as o3d
import torch
import numpy as np
import os

def read_point_cloud(filename):
    return o3d.io.read_point_cloud(filename)

def write_point_cloud(point_cloud, filename):
    o3d.io.write_point_cloud(filename, point_cloud)

def quaternion_to_rotation_matrix(q):
    q = q / (torch.norm(q) + 1e-8)
    w, x, y, z = q
    R = torch.tensor([
        [1 - 2*y*y - 2*z*z, 2*x*y - 2*z*w, 2*x*z + 2*y*w],
        [2*x*y + 2*z*w, 1 - 2*x*x - 2*z*z, 2*y*z - 2*x*w],
        [2*x*z - 2*y*w, 2*y*z + 2*x*w, 1 - 2*x*x - 2*y*y]
    ], device=q.device)
    return R

def quaternion_to_euler(q):
    w, x, y, z = q
    sinr_cosp = 2 * (w * x + y * z)
    cosr_cosp = 1 - 2 * (x * x + y * y)
    roll = torch.atan2(sinr_cosp, cosr_cosp)
    sinp = 2 * (w * y - z * x)
    pitch = torch.where(torch.abs(sinp) >= 1, torch.sign(sinp) * torch.tensor(np.pi / 2), torch.asin(sinp))
    siny_cosp = 2 * (w * z + x * y)
    cosy_cosp = 1 - 2 * (y * y + z * z)
    yaw = torch.atan2(siny_cosp, cosy_cosp)
    return torch.stack([roll, pitch, yaw])

def fit_plane(points):
    centroid = torch.mean(points, dim=0)
    centered_points = points - centroid
    cov = torch.matmul(centered_points.T, centered_points) / (points.shape[0] - 1)
    eigenvalues, eigenvectors = torch.linalg.eigh(cov)
    normal = eigenvectors[:, 0]
    return normal / torch.norm(normal)

def coplanarity_loss(normal1, normal2):
    dot_product = torch.abs(torch.dot(normal1, normal2))
    return 1 - dot_product

def chamfer_distance(params, scale, source, target, source_normal, target_normal, device, num_samples=5000, coplanarity_weight=0.1):
    quaternion = params[:4]
    translation = params[4:]
    R = quaternion_to_rotation_matrix(quaternion)
    transformed_source = scale * (torch.matmul(source, R.T) + translation)
    
    def compute_min_distances(x, y):
        idx_x = torch.randint(x.shape[0], (num_samples,), device=device)
        idx_y = torch.randint(y.shape[0], (num_samples,), device=device)
        x_sampled = x[idx_x]
        y_sampled = y[idx_y]
        distances = torch.sum((x_sampled[:, None, :] - y_sampled[None, :, :]) ** 2, dim=-1)
        min_distances = torch.min(distances, dim=-1)[0]
        return min_distances

    dist1 = compute_min_distances(transformed_source, target)
    dist2 = compute_min_distances(target, transformed_source)
    
    chamfer_loss = torch.mean(dist1) + torch.mean(dist2)
    
    transformed_normal = torch.matmul(R, source_normal)
    coplanarity = coplanarity_loss(transformed_normal, target_normal)
    
    total_loss = (1 - coplanarity_weight) * chamfer_loss + coplanarity_weight * coplanarity
    
    return total_loss, chamfer_loss, coplanarity

def align_point_clouds(newscan: str, target: str, num_iterations=10000, patience=500, 
                       min_delta=1e-4, coplanarity_weight=0.1, lr_rotation=0.1, lr_translation=0.001):
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print(f"Using device: {device}")

    source = read_point_cloud(newscan)
    target = read_point_cloud(target)

    source_tensor = torch.tensor(np.asarray(source.points), dtype=torch.float32, device=device)
    target_tensor = torch.tensor(np.asarray(target.points), dtype=torch.float32, device=device)

    source_normal = fit_plane(source_tensor)
    target_normal = fit_plane(target_tensor)

    quaternion = torch.tensor([1.0, 0.0, 0.0, 0.0], requires_grad=True, device=device)
    translation = torch.zeros(3, requires_grad=True, device=device)
    scale = torch.tensor([1.0], requires_grad=True, device=device)

    # Use separate optimizers for rotation and translation
    optimizer_rotation = torch.optim.Adam([quaternion], lr=lr_rotation)
    optimizer_translation = torch.optim.Adam([translation], lr=lr_translation)
    optimizer_scale = torch.optim.Adam([scale], lr=lr_rotation)  # Use the same learning rate as rotation for scale
    
    best_loss = float('inf')
    best_quaternion = None
    best_translation = None
    best_scale = None
    epochs_no_improve = 0

    for i in range(num_iterations):
        optimizer_rotation.zero_grad()
        optimizer_translation.zero_grad()
        optimizer_scale.zero_grad()

        params = torch.cat((quaternion, translation))
        loss, chamfer_loss, coplanarity = chamfer_distance(
            params, scale, source_tensor, target_tensor, source_normal, target_normal, device, 
            coplanarity_weight=coplanarity_weight
        )
        loss.backward()

        optimizer_rotation.step()
        optimizer_translation.step()
        optimizer_scale.step()
        
        with torch.no_grad():
            quaternion.div_(torch.norm(quaternion))
            scale.clamp_(min=0.1)
        
        if i % 100 == 0:
            print(f"Iteration {i}, Total Loss: {loss.item():.6f}, Chamfer Loss: {chamfer_loss.item():.6f}, Coplanarity: {coplanarity.item():.6f}")

        if loss.item() < best_loss - min_delta:
            best_loss = loss.item()
            best_quaternion = quaternion.clone().detach()
            best_translation = translation.clone().detach()
            best_scale = scale.clone().detach()
            epochs_no_improve = 0
        else:
            epochs_no_improve += 1

        if epochs_no_improve == patience:
            print(f"Early stopping triggered at iteration {i}")
            break

    return best_loss, best_quaternion, best_translation, best_scale

def tune_coplanarity_weight(newscan: str, target: str, weights, num_iterations=1000, patience=50, 
                            min_delta=1e-4, lr_rotation=0.01, lr_translation=0.0001):
    best_weight = None
    best_loss = float('inf')
    best_quaternion = None
    best_translation = None
    best_scale = None

    for weight in weights:
        print(f"\nTrying coplanarity weight: {weight}")
        loss, quaternion, translation, scale = align_point_clouds(
            newscan, target, num_iterations, patience, min_delta, coplanarity_weight=weight, 
            lr_rotation=lr_rotation, lr_translation=lr_translation)
        
        if loss < best_loss:
            best_loss = loss
            best_weight = weight
            best_quaternion = quaternion
            best_translation = translation
            best_scale = scale

    print(f"\nBest coplanarity weight: {best_weight}")
    print(f"Best loss: {best_loss}")

    # Apply the best transformation
    source = read_point_cloud(newscan)
    device = best_quaternion.device

    with torch.no_grad():
        optimal_quaternion = best_quaternion.cpu().numpy()
        optimal_translation = best_translation.cpu().numpy()
        optimal_scale = best_scale.cpu().numpy()[0]

        R = quaternion_to_rotation_matrix(best_quaternion).cpu().numpy()
        source_aligned = source.rotate(R).translate(optimal_translation).scale(optimal_scale, center=(0, 0, 0))

        optimal_rotation = quaternion_to_euler(best_quaternion).cpu().numpy()

    base, ext = os.path.splitext(newscan)
    output_filename = f"{base}_moved{ext}"

    write_point_cloud(source_aligned, output_filename)

    print(f"Optimal rotation (in radians): {optimal_rotation}")
    print(f"Optimal quaternion: {optimal_quaternion}")
    print(f"Optimal translation: {optimal_translation}")
    print(f"Optimal scale: {optimal_scale}")
    print(f"Transformed point cloud saved as: {output_filename}")

    return output_filename


if __name__ == "__main__":

    weights = [0.0, .005, 0.01, 0.05, 0.1, 0.2, 0.5, .9, 1.0]

    folder = "unregistered"
    newscan_file = os.path.join(folder, "lowdown.ply")
    target_file = os.path.join(folder, "initial.ply")
    tune_coplanarity_weight(newscan_file, target_file, weights)

    newscan_file = os.path.join(folder, "topright.ply")
    tune_coplanarity_weight(newscan_file, target_file, weights)