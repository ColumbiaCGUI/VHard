# -*- coding: utf-8 -*-
"""
Created on Sun Jun 30 22:05:23 2024

@author: JOEL
"""

import os
import multiprocessing
os.environ['OMP_NUM_THREADS'] = str(multiprocessing.cpu_count())
from sklearn.cluster import KMeans

import warnings
warnings.filterwarnings("ignore", message="KMeans is known to have a memory leak")

from io_utils import load_ply, write_ply


def segment(unsegmented, lattice):
    """
    Segments the climbing holds.

    Parameters
    ----------
    unsegmented : str
        Filepath to the cleaned point cloud.
    lattice : np.array
        Centerpoints for each hold.

    Returns
    -------
    final_clusters : dict of o3d
        duh.

    """

    # Load data
    points, colors = load_ply(unsegmented)
    screwholes, _ = load_ply(lattice)

    # Perform spatial KMeans to isolate holds
    k = len(screwholes)
    kmeans = KMeans(n_clusters=k, init=screwholes, n_init=1, max_iter=1)
    labels = kmeans.fit_predict(points)

    # Segment the point cloud based on spatial clustering
    names = [f"{'ABCDEFGHIJK'[j]}{i+1}" for j in range(11) for i in range(18)]
    names.reverse()
    spatial_clusters = {names[i]: labels == i for i in range(k)}
    
    # Output PLY files
    files = list()
    for key, mask in spatial_clusters.items():
        pts = points[mask]
        rgbs = colors[mask]
        filename = os.path.join('holds', f'{key}.ply')
        write_ply(pts, rgbs, filename)
        files.append(filename)
        
    return files

    # # Perform color-based clustering on each spatial cluster
    # for key, mask in spatial_clusters.items():

    #     color_kmeans = KMeans(n_clusters=2, n_init=10)
    #     cluster = colors[mask]
    #     color_labels = color_kmeans.fit_predict(cluster)

    #     for i in range(2):
    #         pts = points[mask][color_labels == i]
    #         rgbs = cluster[color_labels == i]
            
    #         write_ply(pts, rgbs, os.path.join('holds', f'{key}_{i}.ply'))
    #         # TODO: holds need trimesh, not pcd
            
    #     print(f'Segmented {key}')


if __name__ == '__main__':

    unsegmented = 'cleaned_unsegmented.ply'
    lattice = 'lattice.ply'
    segment(unsegmented, lattice)
