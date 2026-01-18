window.TreadmillDisplay = {
    scene: null,
    camera: null,
    renderer: null,
    grid: null,
    forwardIndicator: null,
    forwardIndicatorLine: null,
    shadowFan: null,
    mountains: null,
    trees: [],
    treeContainer: null,
    animationFrameId: null,
    isWalkingModeEnabled: false,
    speed: 0,
    targetSpeed: 0,
    smoothedSpeed: 0,
    horizontal: 0,
    targetHorizontal: 0,
    smoothedHorizontal: 0,
    vertical: 0,
    headRotation: 0,
    gridOffset: 0,
    lastFrameTime: 0,

    // Performance: FPS limiting and visibility detection
    targetFPS: 30,
    lastRenderTime: 0,
    isVisible: true,
    needsRender: true,

    // Smoothing factor (0-1, higher = more responsive, lower = smoother)
    speedSmoothingFactor: 0.15,

    // Cache previous values to avoid unnecessary geometry updates
    prevSpeed: 0,
    prevHorizontal: 0,

    // Distance from camera (higher = closer to viewer)
    visualizationDistance: 7.7,

    // Global speed multiplier for all movement (adjust this to scale overall speed)
    globalSpeedMultiplier: 3.0,

    // Tree configuration
    treeSpacing: 2.5,
    treeCount: 20,
    treeSideOffset: 5,
    treeRowCount: 3, // Multiple rows of trees for forest depth

    // Mountain configuration
    mountainSpacing: 12,
    mountainCount: 8,

    initialize: function(containerId) {
        const container = document.getElementById(containerId);
        if (!container) {
            console.error('Container not found:', containerId);
            return false;
        }

        // Get container dimensions
        const width = container.clientWidth;
        const height = container.clientHeight;

        // Create scene
        this.scene = new THREE.Scene();
        this.scene.background = new THREE.Color(0x121212); // Match app background

        // Create camera
        this.camera = new THREE.PerspectiveCamera(60, width / height, 0.1, 1000);
        this.camera.position.set(0, 3, 10);
        this.camera.lookAt(0, 0, 0);

        // Create renderer with power-saving options
        this.renderer = new THREE.WebGLRenderer({
            antialias: true,
            powerPreference: 'low-power'  // Prefer integrated GPU over discrete
        });
        this.renderer.setSize(width, height);
        this.renderer.setPixelRatio(Math.min(window.devicePixelRatio, 1.5)); // Cap pixel ratio to reduce GPU load
        container.appendChild(this.renderer.domElement);

        // Create retrowave grid
        this.createGrid();

        // Create wireframe mountains in background
        this.createMountains();

        // Create trees on left and right
        this.createTrees();

        // Create forward direction indicator
        this.createForwardIndicator();

        // Create shadow fan effect
        this.createShadowFan();

        // Add lighting
        const ambientLight = new THREE.AmbientLight(0x404040, 0.5);
        this.scene.add(ambientLight);

        // Handle window resize
        window.addEventListener('resize', () => this.onWindowResize(containerId));

        // Handle visibility changes to pause rendering when hidden
        document.addEventListener('visibilitychange', () => {
            this.isVisible = !document.hidden;
            if (this.isVisible) {
                this.lastRenderTime = 0; // Reset to render immediately when visible again
                this.needsRender = true;
            }
        });

        // Start animation loop
        this.animate();

        console.log('Three.js display initialized');
        return true;
    },

    createGrid: function() {
        const gridSize = 100;
        const gridDivisions = 50;
        const gridColor = 0x2c2c2c; // Border color from variables

        // Create grid
        const grid = new THREE.GridHelper(gridSize, gridDivisions, gridColor, gridColor);
        grid.material.opacity = 0.3;
        grid.material.transparent = true;
        grid.position.y = 0;
        grid.rotation.x = 0;
        this.grid = grid;
        this.scene.add(grid);

        // Add fog for depth (near, far) - reduce far distance to bring horizon closer
        this.scene.fog = new THREE.Fog(0x121212, 5, 30);
    },

    createMountains: function() {
        this.mountains = [];

        const totalLength = this.mountainCount * this.mountainSpacing;

        // Create mountains on both sides that will move with the floor
        for (let i = 0; i < this.mountainCount; i++) {
            const zPos = -i * this.mountainSpacing - 15;

            // Left side mountains (further out, behind trees)
            const leftMountain = this.createMountainPeak(
                5 + Math.random() * 6,  // Random height 5-11
                0.8 + Math.random() * 0.6  // Random scale 0.8-1.4
            );
            leftMountain.position.set(-12 - Math.random() * 8, 0, zPos);
            this.scene.add(leftMountain);
            this.mountains.push(leftMountain);

            // Right side mountains
            const rightMountain = this.createMountainPeak(
                5 + Math.random() * 6,
                0.8 + Math.random() * 0.6
            );
            rightMountain.position.set(12 + Math.random() * 8, 0, zPos);
            this.scene.add(rightMountain);
            this.mountains.push(rightMountain);
        }
    },

    createMountainPeak: function(height, scale) {
        const group = new THREE.Group();

        // Accent color - cyan/teal
        const accentColor = 0x00d4aa;
        const darkColor = 0x0a2a22;

        const baseWidth = 8 * scale;
        const baseDepth = 5 * scale;

        // Mountain vertices
        const peak = new THREE.Vector3(0, height, 0);
        const frontLeft = new THREE.Vector3(-baseWidth / 2, 0, baseDepth / 2);
        const frontRight = new THREE.Vector3(baseWidth / 2, 0, baseDepth / 2);
        const backLeft = new THREE.Vector3(-baseWidth / 2, 0, -baseDepth / 2);
        const backRight = new THREE.Vector3(baseWidth / 2, 0, -baseDepth / 2);

        // Create filled faces for each side of the pyramid
        const faces = [
            { vertices: [frontLeft, frontRight, peak], color: accentColor, opacity: 0.25 },  // Front face
            { vertices: [frontRight, backRight, peak], color: 0x00a080, opacity: 0.2 },      // Right face
            { vertices: [backRight, backLeft, peak], color: darkColor, opacity: 0.15 },       // Back face
            { vertices: [backLeft, frontLeft, peak], color: 0x00a080, opacity: 0.2 },         // Left face
        ];

        faces.forEach(face => {
            const geometry = new THREE.BufferGeometry();
            const vertices = new Float32Array([
                face.vertices[0].x, face.vertices[0].y, face.vertices[0].z,
                face.vertices[1].x, face.vertices[1].y, face.vertices[1].z,
                face.vertices[2].x, face.vertices[2].y, face.vertices[2].z,
            ]);
            geometry.setAttribute('position', new THREE.BufferAttribute(vertices, 3));
            geometry.computeVertexNormals();

            const material = new THREE.MeshBasicMaterial({
                color: face.color,
                transparent: true,
                opacity: face.opacity,
                side: THREE.DoubleSide
            });

            const mesh = new THREE.Mesh(geometry, material);
            group.add(mesh);
        });

        // Wireframe edges for definition
        const edgeMaterial = new THREE.LineBasicMaterial({
            color: accentColor,
            transparent: true,
            opacity: 0.6
        });

        // Draw edges from base to peak
        const corners = [frontLeft, frontRight, backRight, backLeft];
        corners.forEach(corner => {
            const geometry = new THREE.BufferGeometry().setFromPoints([corner, peak]);
            const line = new THREE.Line(geometry, edgeMaterial);
            group.add(line);
        });

        // Base rectangle wireframe
        const basePoints = [...corners, corners[0]];
        const baseGeometry = new THREE.BufferGeometry().setFromPoints(basePoints);
        const baseLine = new THREE.Line(baseGeometry, edgeMaterial);
        group.add(baseLine);

        // Horizontal contour lines for extra detail
        const contourLevels = [0.3, 0.6];
        contourLevels.forEach(t => {
            const y = t * height;
            const w = baseWidth * (1 - t * 0.8) / 2;
            const d = baseDepth * (1 - t * 0.8) / 2;

            const contourMaterial = new THREE.LineBasicMaterial({
                color: accentColor,
                transparent: true,
                opacity: 0.4 - t * 0.2
            });

            const points = [
                new THREE.Vector3(-w, y, d),
                new THREE.Vector3(w, y, d),
                new THREE.Vector3(w, y, -d),
                new THREE.Vector3(-w, y, -d),
                new THREE.Vector3(-w, y, d),
            ];
            const geometry = new THREE.BufferGeometry().setFromPoints(points);
            const line = new THREE.Line(geometry, contourMaterial);
            group.add(line);
        });

        return group;
    },

    createTrees: function() {
        this.trees = [];

        const totalLength = this.treeCount * this.treeSpacing;

        // Create multiple rows of trees for forest depth
        for (let row = 0; row < this.treeRowCount; row++) {
            const rowOffset = row * 2.5; // Distance between rows
            const xOffset = this.treeSideOffset + rowOffset;
            const zStagger = row * 1.2; // Stagger each row slightly

            for (let i = 0; i < this.treeCount; i++) {
                const zPos = -i * this.treeSpacing - zStagger;
                const randomOffset = (Math.random() - 0.5) * 1.5; // Random z variation

                // Left forest
                const leftTree = this.createTree(row);
                leftTree.position.set(
                    -xOffset - Math.random() * 1.5,
                    0,
                    zPos + randomOffset
                );
                this.scene.add(leftTree);
                this.trees.push(leftTree);

                // Right forest
                const rightTree = this.createTree(row);
                rightTree.position.set(
                    xOffset + Math.random() * 1.5,
                    0,
                    zPos + randomOffset
                );
                this.scene.add(rightTree);
                this.trees.push(rightTree);
            }
        }
    },

    createTree: function(rowIndex = 0) {
        const group = new THREE.Group();

        // Green color palette - darker for back rows, brighter for front
        const greenColors = [
            0x2d5a3d,  // Front row - brighter green
            0x234a30,  // Middle row
            0x1a3a25,  // Back row - darker green
        ];
        const greenColor = greenColors[Math.min(rowIndex, greenColors.length - 1)];
        const darkerGreen = greenColors[Math.min(rowIndex + 1, greenColors.length - 1)];

        const trunkColor = 0x3d2817;  // Brown trunk

        const baseOpacity = 0.5 - rowIndex * 0.1;

        // Random tree size variation
        const sizeScale = 0.7 + Math.random() * 0.6;

        // Tree trunk (simple line)
        const trunkHeight = 1.2 * sizeScale;
        const trunkMaterial = new THREE.LineBasicMaterial({
            color: trunkColor,
            transparent: true,
            opacity: 0.6
        });
        const trunkGeometry = new THREE.BufferGeometry().setFromPoints([
            new THREE.Vector3(0, 0, 0),
            new THREE.Vector3(0, trunkHeight, 0)
        ]);
        const trunk = new THREE.Line(trunkGeometry, trunkMaterial);
        group.add(trunk);

        // Tree foliage layers - pine tree style with filled faces
        const foliageLayers = [
            { y: trunkHeight * 0.6, width: 1.4 * sizeScale, height: 0.9 * sizeScale },
            { y: trunkHeight * 0.6 + 0.5 * sizeScale, width: 1.1 * sizeScale, height: 0.8 * sizeScale },
            { y: trunkHeight * 0.6 + 0.9 * sizeScale, width: 0.8 * sizeScale, height: 0.7 * sizeScale },
            { y: trunkHeight * 0.6 + 1.2 * sizeScale, width: 0.5 * sizeScale, height: 0.5 * sizeScale },
        ];

        foliageLayers.forEach((layer, layerIndex) => {
            const halfWidth = layer.width / 2;
            const depth = halfWidth * 0.4;
            const tipY = layer.y + layer.height;

            // Define the 4 base corners and tip
            const frontLeft = new THREE.Vector3(-halfWidth, layer.y, depth);
            const frontRight = new THREE.Vector3(halfWidth, layer.y, depth);
            const backLeft = new THREE.Vector3(-halfWidth, layer.y, -depth);
            const backRight = new THREE.Vector3(halfWidth, layer.y, -depth);
            const tip = new THREE.Vector3(0, tipY, 0);

            // Create filled triangular faces
            const faces = [
                { verts: [frontLeft, frontRight, tip], color: greenColor, opacity: baseOpacity },      // Front
                { verts: [frontRight, backRight, tip], color: darkerGreen, opacity: baseOpacity * 0.8 }, // Right
                { verts: [backRight, backLeft, tip], color: darkerGreen, opacity: baseOpacity * 0.6 },   // Back
                { verts: [backLeft, frontLeft, tip], color: darkerGreen, opacity: baseOpacity * 0.8 },   // Left
            ];

            faces.forEach(face => {
                const geometry = new THREE.BufferGeometry();
                const vertices = new Float32Array([
                    face.verts[0].x, face.verts[0].y, face.verts[0].z,
                    face.verts[1].x, face.verts[1].y, face.verts[1].z,
                    face.verts[2].x, face.verts[2].y, face.verts[2].z,
                ]);
                geometry.setAttribute('position', new THREE.BufferAttribute(vertices, 3));

                const material = new THREE.MeshBasicMaterial({
                    color: face.color,
                    transparent: true,
                    opacity: face.opacity,
                    side: THREE.DoubleSide
                });

                const mesh = new THREE.Mesh(geometry, material);
                group.add(mesh);
            });

            // Wireframe edges for definition
            const edgeMaterial = new THREE.LineBasicMaterial({
                color: greenColor,
                transparent: true,
                opacity: baseOpacity + 0.2
            });

            // Edge lines from corners to tip
            [frontLeft, frontRight, backLeft, backRight].forEach(corner => {
                const edgeGeometry = new THREE.BufferGeometry().setFromPoints([corner, tip]);
                const edge = new THREE.Line(edgeGeometry, edgeMaterial);
                group.add(edge);
            });

            // Base outline
            const basePoints = [frontLeft, frontRight, backRight, backLeft, frontLeft];
            const baseGeometry = new THREE.BufferGeometry().setFromPoints(basePoints);
            const baseLine = new THREE.Line(baseGeometry, edgeMaterial);
            group.add(baseLine);
        });

        // Random Y rotation for variety
        group.rotation.y = Math.random() * Math.PI * 2;

        return group;
    },

    createForwardIndicator: function() {
        // Create a flexible line that bends based on direction
        this.forwardIndicator = new THREE.Group();
        this.forwardIndicator.position.set(0, 0.5, this.visualizationDistance); // Base position at bottom of screen (near camera)
        this.forwardIndicator.visible = false;

        // Create a curved tube geometry with multiple bend points for smoother flexing
        const points = [
            new THREE.Vector3(0, 0, 0),      // Start (bottom of screen, near camera)
            new THREE.Vector3(0, 0, -1),     // Bend point 1
            new THREE.Vector3(0, 0, -2),     // Bend point 2
            new THREE.Vector3(0, 0, -3),     // Bend point 3
            new THREE.Vector3(0, 0, -4)      // End (far from camera) - extends forward
        ];
        const curve = new THREE.CatmullRomCurve3(points);

        const tubeGeometry = new THREE.TubeGeometry(curve, 40, 0.04, 8, false);
        const material = new THREE.MeshBasicMaterial({
            color: 0xe0e0e0,
            transparent: false
        });

        this.forwardIndicatorLine = new THREE.Mesh(tubeGeometry, material);
        this.forwardIndicator.add(this.forwardIndicatorLine);
        this.scene.add(this.forwardIndicator);
    },

    createShadowFan: function() {
        // Create an arched fan shape with multiple segments for smooth curve
        const fanGeometry = new THREE.BufferGeometry();

        // Will be updated dynamically - reserve space for multiple triangles
        // Create a mesh grid: length segments × width segments
        const lengthSegments = 8;  // Along the length (distance from origin)
        const widthSegments = 6;   // Across the width (arc)
        const quads = lengthSegments * widthSegments;
        const verticesPerQuad = 6; // 2 triangles × 3 vertices
        const totalVertices = quads * verticesPerQuad;

        const vertices = new Float32Array(totalVertices * 3);
        fanGeometry.setAttribute('position', new THREE.BufferAttribute(vertices, 3));

        const fanMaterial = new THREE.MeshBasicMaterial({
            color: 0xe0e0e0, // White color
            transparent: true,
            opacity: 0.3,
            side: THREE.DoubleSide
        });

        this.shadowFan = new THREE.Mesh(fanGeometry, fanMaterial);
        this.shadowFan.visible = false;
        this.scene.add(this.shadowFan);
    },

    setSpeed: function(speed) {
        this.targetSpeed = speed;
    },

    setWalkingMode: function(enabled) {
        this.isWalkingModeEnabled = enabled;

        // Reset cached values to force geometry update on enable/disable
        this.prevSpeed = -1; // Force update on next frame
        this.prevHorizontal = -999; // Force update on next frame

        // Show/hide forward indicator and shadow fan based on walking mode
        if (this.forwardIndicator) {
            this.forwardIndicator.visible = enabled;
        }
        if (this.shadowFan) {
            this.shadowFan.visible = enabled;
        }
    },

    setWalkingDirection: function(horizontal, vertical) {
        this.targetHorizontal = horizontal;
        this.vertical = vertical;
    },

    setHeadRotation: function(rotation) {
        this.headRotation = rotation;
    },

    animate: function() {
        this.animationFrameId = requestAnimationFrame(() => this.animate());

        const now = performance.now();

        // Skip rendering if not visible (window hidden/minimized)
        if (!this.isVisible) {
            return;
        }

        // FPS limiting - skip frame if not enough time has passed
        const fpsInterval = 1000 / this.targetFPS;
        if (this.lastRenderTime && (now - this.lastRenderTime) < fpsInterval) {
            return;
        }

        const deltaTime = this.lastFrameTime ? (now - this.lastFrameTime) / 1000 : 0.016;
        this.lastFrameTime = now;
        this.lastRenderTime = now;

        // Smooth interpolation towards target values (frame-rate independent)
        const smoothFactor = 1 - Math.pow(1 - this.speedSmoothingFactor, deltaTime * 60);
        this.speed = this.speed + (this.targetSpeed - this.speed) * smoothFactor;
        this.horizontal = this.horizontal + (this.targetHorizontal - this.horizontal) * smoothFactor;

        const time = Date.now() * 0.001;

        if (this.grid) {
            if (!this.isWalkingModeEnabled) {
                // Idle state - static grid
                this.grid.position.y = 0;
                this.grid.position.z = 0;

                // Gentle sway for trees in idle
                if (this.trees && this.trees.length > 0) {
                    this.trees.forEach((tree, index) => {
                        tree.rotation.z = Math.sin(time * 0.5 + index * 0.3) * 0.015;
                    });
                }

                // Subtle movement for mountains in idle
                if (this.mountains && this.mountains.length > 0) {
                    this.mountains.forEach((mountain, index) => {
                        mountain.position.y = Math.sin(time * 0.3 + index * 0.5) * 0.1;
                    });
                }
            } else {
                // Walking mode enabled - everything moves together based on walking velocity
                this.grid.position.y = 0;

                // Reset mountain Y positions when walking
                if (this.mountains && this.mountains.length > 0) {
                    this.mountains.forEach(mountain => {
                        mountain.position.y = 0;
                    });
                }

                // Reset tree rotation when walking
                if (this.trees && this.trees.length > 0) {
                    this.trees.forEach(tree => {
                        tree.rotation.z = 0;
                    });
                }

                // Calculate movement delta - same for all elements (grid, trees, mountains)
                const visualSpeed = this.speed * this.globalSpeedMultiplier;
                const movementDelta = visualSpeed * deltaTime;

                // Update grid offset (grid seamlessly loops every 2 units)
                this.gridOffset += movementDelta;
                if (this.gridOffset > 2) {
                    this.gridOffset -= 2;
                }
                this.grid.position.z = this.gridOffset;

                // Move trees with the grid (same speed)
                if (this.trees && this.trees.length > 0) {
                    const totalTreeLength = this.treeCount * this.treeSpacing;
                    this.trees.forEach(tree => {
                        tree.position.z += movementDelta;

                        // Reset tree position when it passes the camera (seamless loop)
                        if (tree.position.z > 10) {
                            tree.position.z -= totalTreeLength;
                        }
                    });
                }

                // Move mountains with the grid (same speed - no parallax)
                if (this.mountains && this.mountains.length > 0) {
                    const totalMountainLength = this.mountainCount * this.mountainSpacing;
                    this.mountains.forEach(mountain => {
                        mountain.position.z += movementDelta;

                        // Reset mountain position when it passes the camera
                        if (mountain.position.z > 5) {
                            mountain.position.z -= totalMountainLength;
                        }
                    });
                }
            }
        }

        // Check if values changed significantly (shared by both line and shadow)
        const speedDiff = Math.abs(this.speed - this.prevSpeed);
        const horizontalDiff = Math.abs(this.horizontal - this.prevHorizontal);
        const threshold = 0.01; // Only update if change is > 1%
        const needsUpdate = speedDiff > threshold || horizontalDiff > threshold;

        // Update forward indicator to follow a semi-circular arc
        if (this.forwardIndicator && this.forwardIndicator.visible && this.forwardIndicatorLine) {
            if (needsUpdate) {
                // horizontal is -1 (left) to 1 (right)
                // Map horizontal to angle: -1 → 90°, 0 → 0°, 1 → -90° (negated to match view direction)
                const angle = -this.horizontal * (Math.PI / 2);

                // Create points along a circular arc
                // Each point at increasing distance from origin, following the same angle
                const points = [];
                const numPoints = 5;
                const maxDistanceAtFullSpeed = 4;
                // Scale distance based on speed (0-1 range)
                const maxDistance = Math.max(0.5, maxDistanceAtFullSpeed * this.speed); // Minimum 0.5 to keep visible

                for (let i = 0; i < numPoints; i++) {
                    const distance = (i / (numPoints - 1)) * maxDistance;
                    const x = distance * Math.sin(angle);
                    const z = -distance * Math.cos(angle); // Negative because forward is -z
                    points.push(new THREE.Vector3(x, 0, z));
                }

                const curve = new THREE.CatmullRomCurve3(points);

                // Update the tube geometry
                const newGeometry = new THREE.TubeGeometry(curve, 40, 0.04, 8, false);
                this.forwardIndicatorLine.geometry.dispose();
                this.forwardIndicatorLine.geometry = newGeometry;
            }
        }

        // Update shadow fan to create a curved arched swept area
        if (this.shadowFan && this.shadowFan.visible) {
            if (needsUpdate) {
                // Same angle calculation as the line (negated to match view direction)
                const angle = -this.horizontal * (Math.PI / 2);
                const maxDistanceAtFullSpeed = 4;
                // Scale distance based on speed (0-1 range) - same as line
                const maxDistance = Math.max(0.5, maxDistanceAtFullSpeed * this.speed);
                const lengthSegments = 8;
                const widthSegments = 6;

                const positions = this.shadowFan.geometry.attributes.position.array;
                let vertexIndex = 0;

                // Create a curved mesh grid
                for (let i = 0; i < lengthSegments; i++) {
                    for (let j = 0; j < widthSegments; j++) {
                        // Length parameters (distance from origin)
                        const t0 = i / lengthSegments;
                        const t1 = (i + 1) / lengthSegments;
                        const dist0 = t0 * maxDistance;
                        const dist1 = t1 * maxDistance;

                        // Width parameters (angle from center to arc)
                        const w0 = j / widthSegments;
                        const w1 = (j + 1) / widthSegments;
                        const angle0 = w0 * angle;
                        const angle1 = w1 * angle;

                        // Calculate 4 corners of this quad following circular arcs
                        // Bottom-left
                        const x00 = dist0 * Math.sin(angle0);
                        const z00 = -dist0 * Math.cos(angle0);

                        // Bottom-right
                        const x01 = dist0 * Math.sin(angle1);
                        const z01 = -dist0 * Math.cos(angle1);

                        // Top-left
                        const x10 = dist1 * Math.sin(angle0);
                        const z10 = -dist1 * Math.cos(angle0);

                        // Top-right
                        const x11 = dist1 * Math.sin(angle1);
                        const z11 = -dist1 * Math.cos(angle1);

                        // Create two triangles for this quad
                        // Triangle 1: (0,0), (0,1), (1,0)
                        positions[vertexIndex++] = x00;
                        positions[vertexIndex++] = 0.5;
                        positions[vertexIndex++] = this.visualizationDistance + z00;

                        positions[vertexIndex++] = x01;
                        positions[vertexIndex++] = 0.5;
                        positions[vertexIndex++] = this.visualizationDistance + z01;

                        positions[vertexIndex++] = x10;
                        positions[vertexIndex++] = 0.5;
                        positions[vertexIndex++] = this.visualizationDistance + z10;

                        // Triangle 2: (0,1), (1,1), (1,0)
                        positions[vertexIndex++] = x01;
                        positions[vertexIndex++] = 0.5;
                        positions[vertexIndex++] = this.visualizationDistance + z01;

                        positions[vertexIndex++] = x11;
                        positions[vertexIndex++] = 0.5;
                        positions[vertexIndex++] = this.visualizationDistance + z11;

                        positions[vertexIndex++] = x10;
                        positions[vertexIndex++] = 0.5;
                        positions[vertexIndex++] = this.visualizationDistance + z10;
                    }
                }

                this.shadowFan.geometry.attributes.position.needsUpdate = true;
            }

            // Always update opacity (outside threshold check so shadow is always visible)
            this.shadowFan.material.opacity = 0.25 + Math.abs(this.horizontal) * 0.25;
        }

        // Update cached values after both line and shadow have been processed
        if (needsUpdate) {
            this.prevSpeed = this.speed;
            this.prevHorizontal = this.horizontal;
        }

        // Camera always looks forward, no rotation

        if (this.renderer && this.scene && this.camera) {
            this.renderer.render(this.scene, this.camera);
        }
    },

    onWindowResize: function(containerId) {
        const container = document.getElementById(containerId);
        if (!container || !this.camera || !this.renderer) return;

        const width = container.clientWidth;
        const height = container.clientHeight;

        this.camera.aspect = width / height;
        this.camera.updateProjectionMatrix();
        this.renderer.setSize(width, height);
    },

    updateSpeed: function(speed) {
        // You can use this to update the visualization based on treadmill speed
        if (this.cube) {
            // Example: rotate faster based on speed
            this.cube.rotation.speed = speed;
        }
    },

    dispose: function() {
        if (this.animationFrameId) {
            cancelAnimationFrame(this.animationFrameId);
        }

        if (this.renderer) {
            const container = this.renderer.domElement.parentElement;
            if (container) {
                container.removeChild(this.renderer.domElement);
            }
            this.renderer.dispose();
        }

        if (this.scene) {
            this.scene.traverse((object) => {
                if (object.geometry) {
                    object.geometry.dispose();
                }
                if (object.material) {
                    if (Array.isArray(object.material)) {
                        object.material.forEach(material => material.dispose());
                    } else {
                        object.material.dispose();
                    }
                }
            });
        }

        this.scene = null;
        this.camera = null;
        this.renderer = null;
        this.grid = null;
        this.mountains = null;
        this.trees = [];
        this.treeContainer = null;

        console.log('Three.js display disposed');
    }
};
