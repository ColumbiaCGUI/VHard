# -*- coding: utf-8 -*-
"""
Created on Mon Jul  1 11:31:39 2024

@author: JOEL
"""

import open3d as o3d
import numpy as np


class Hold(o3d.geometry.TriangleMesh):
    
    idx : str
    screwhole : np.array
    orientation : float = 0
    
    def __init__(self, idx):
        
        self.idx = idx
        
    def set_orientation(self, screwhole, orientation):
        
        self.screwhole = screwhole / np.linalg.norm(screwhole)
        self.orientation = orientation
    
        if self.orientation:
            
            cos_theta = np.cos(self.orientation)
            sin_theta = np.sin(self.orientation)
            cross_matrix = np.array([
                [0, -self.screwhole[2], self.screwhole[1]],
                [self.screwhole[2], 0, -self.screwhole[0]],
                [-self.screwhole[1], self.screwhole[0], 0]
            ])
            rotation_matrix = (
                cos_theta * np.eye(3) +
                (1 - cos_theta) * np.outer(self.screwhole, self.screwhole) +
                sin_theta * cross_matrix
            )
            
            self.vertices = o3d.utility.Vector3dVector(
                np.asarray(self.vertices) @ rotation_matrix.T
            )


class Route:
    
    holds : list
    start : list
    end : list
    

class Wall(o3d.geometry.TriangleMesh):
    
    holds : list
    routes : list
    

class MoonBoard(Wall):
    
    H = 18
    W = 11
    screwholes = np.empty((H, W, 3))