// graphViewer.js - Place this in your wwwroot/js folder

// Store module documentation globally
let globalModuleDocs = {};
let dotNetHelper = null;

// Load required dependencies
export async function loadDependencies() {
    // Create a function to load script and return a promise
    const loadScript = (src) => {
        return new Promise((resolve, reject) => {
            const script = document.createElement('script');
            script.src = src;
            script.onload = () => resolve();
            script.onerror = () => reject(new Error(`Failed to load script: ${src}`));
            document.head.appendChild(script);
        });
    };

    try {
        // Load jQuery if not already loaded
        if (!window.jQuery) {
            await loadScript("https://code.jquery.com/jquery-3.6.0.min.js");
        }

        // Load D3.js if not already loaded
        if (!window.d3) {
            await loadScript("https://d3js.org/d3.v7.min.js");
        }

        // Load Monaco Editor
        if (!window.monaco) {
            await loadScript("https://cdn.jsdelivr.net/npm/monaco-editor@0.43.0/min/vs/loader.js");

            // Configure Monaco
            require.config({ paths: { 'vs': 'https://cdn.jsdelivr.net/npm/monaco-editor@0.43.0/min/vs' } });
            await new Promise((resolve) => {
                require(['vs/editor/editor.main'], function () {
                    resolve();
                });
            });
        }

        // Load Font Awesome if not already loaded
        if (!document.querySelector('script[src*="font-awesome"]')) {
            await loadScript("https://cdnjs.cloudflare.com/ajax/libs/font-awesome/6.4.0/js/all.min.js");
        }

        return true;
    } catch (error) {
        console.error("Failed to load dependencies:", error);
        return false;
    }
}

// Initialize the graph
export async function initializeGraph(data, dotNetRef) {
    // Store the .NET reference for callbacks
    dotNetHelper = dotNetRef;

    // Load dependencies first
    const loaded = await loadDependencies();
    if (!loaded) {
        console.error("Failed to initialize graph: dependencies not loaded");
        return;
    }

    // Ensure the container exists
    const container = document.querySelector('.graph-container');
    if (!container) {
        console.error("Graph container not found");
        return;
    }

    // Render the graph
    renderGraph(data, container);

    // Set up search functionality
    setupSearch();
}

// The main function to render the graph
function renderGraph(data, container) {
    // Debug logging
    console.log('renderGraph called with:', {
        data,
        container,
        containerDimensions: container ? {
            width: container.clientWidth || $(container).width(),
            height: container.clientHeight || $(container).height()
        } : null,
        d3Available: !!window.d3
    });

    // Validate dependencies
    if (!window.d3) {
        console.error('D3 library not loaded');
        return;
    }

    // Get existing SVG element
    const svg = d3.select("#orgChart");
    if (svg.empty()) {
        console.error('Could not find SVG element with id "orgChart"');
        return;
    }

    // Clear existing content
    svg.selectAll("*").remove();

    // Update SVG dimensions based on container
    const w = container.clientWidth;
    const h = container.clientHeight;
    svg.attr("width", w)
        .attr("height", h);

    // Create main group for transformations
    const g = svg.append("g")
        .attr("transform", "translate(60,60)");

    // Create a temporary group for text measurements
    const measureGroup = svg.append("g")
        .style("visibility", "hidden");

    // Calculate node dimensions
    function getNodeDimensions(d) {
        measureGroup.selectAll("*").remove();
        const nameText = measureGroup.append("text")
            .attr("class", "node-text")
            .text(d.name);
        const projectText = measureGroup.append("text")
            .attr("class", "node-text")
            .text(d.project);

        const nameBox = nameText.node().getBBox();
        const projectBox = projectText.node().getBBox();

        const width = Math.max(nameBox.width, projectBox.width) + 40; // Adding padding
        const height = 46; // Fixed height as it's consistent

        return { width, height };
    }

    // Group nodes by level and calculate positions
    function calculateNodePositions() {
        const levelGroups = {};
        const minSpacing = 30; // Minimum space between nodes

        if (data.nodes.length === 1) {
            const node = data.nodes[0];
            node.dimensions = getNodeDimensions(node);
            node.x = 0; // Center of the view
            node.y = 0;
            return;
        }

        // First, group nodes by level and calculate their dimensions
        data.nodes.forEach(node => {
            // If no edges exist, treat all nodes as level 0
            const nodeLevel = data.edges.length === 0 ? 0 : (node.level || 0);
            if (!levelGroups[nodeLevel]) {
                levelGroups[nodeLevel] = [];
            }
            node.dimensions = getNodeDimensions(node);
            levelGroups[nodeLevel].push(node);
        });

        // Calculate positions for each level
        const levelHeight = 120; // Vertical spacing between levels

        Object.entries(levelGroups).forEach(([level, nodes]) => {
            // Calculate total width needed for this level
            const totalWidth = nodes.reduce((sum, node, i) => {
                return sum + node.dimensions.width + (i < nodes.length - 1 ? minSpacing : 0);
            }, 0);

            let currentX = -totalWidth / 2; // Center the level

            // Position each node
            nodes.forEach((node, i) => {
                node.x = currentX + (node.dimensions.width / 2);
                node.y = level * levelHeight;
                currentX += node.dimensions.width + minSpacing;
            });
        });
    }

    calculateNodePositions();
    measureGroup.remove();

    // Create edges group and edges
    const edgesGroup = g.append("g").attr("class", "edges");
    const edges = edgesGroup.selectAll(".link")
        .data(data.edges)
        .join("path")
        .attr("class", "link")
        .style("stroke", d => {
            const sourceNode = data.nodes.find(n => n.id === d?.source);
            return sourceNode?.color || "#808080";
        });

    // Create nodes group and nodes
    const nodesGroup = g.append("g").attr("class", "nodes");
    const nodes = nodesGroup.selectAll(".node-group")
        .data(data.nodes)
        .join("g")
        .attr("class", "node-group")
        .attr("transform", d => `translate(${d.x},${d.y})`)
        .on("click", (evt, d) => {
            evt.stopPropagation();
            highlightNode(d);
            if (dotNetHelper) {
                dotNetHelper.invokeMethodAsync('OnNodeSelected', d.id.toString());
            }
        })
        .on("contextmenu", showContextMenu);

    // Create node elements with dynamic widths
    nodes.each(function (d) {
        const nodeGroup = d3.select(this);

        nodeGroup.append("rect")
            .attr("class", "node-rect")
            .attr("width", d.dimensions.width)
            .attr("height", d.dimensions.height)
            .attr("x", -d.dimensions.width / 2)
            .attr("y", -20)
            .style("stroke", d => d.color);

        nodeGroup.append("text")
            .attr("class", "node-text project-text")
            .attr("text-anchor", "middle")
            .attr("y", -3)
            .attr("font-size", 11)
            .text(d.project);

        nodeGroup.append("text")
            .attr("class", "node-text name-text")
            .attr("y", 12)
            .attr("text-anchor", "middle")
            .attr("fill", "#00ffff")
            .text(d.name);
    });

    // Path generator for edges
    const lineGen = d3.line().curve(d3.curveBasis);
    edges.attr("d", d => {
        const source = data.nodes.find(n => n.id === d.source);
        const target = data.nodes.find(n => n.id === d.target);

        const midY = (source.y + target.y) / 2;
        const sourceOffset = source.level === 0 ? 20 : 0;

        return lineGen([
            [source.x, source.y],
            [source.x, source.y + sourceOffset],
            [source.x, midY],
            [target.x, midY],
            [target.x, target.y - 20],
            [target.x, target.y]
        ]);
    });

    // Highlight node and its descendants
    function highlightNode(node) {
        // Reset all to dimmed state first
        const nodeGroups = d3.selectAll(".node-group")
            .classed("dimmed", true);

        // Dim node rectangles while preserving their original colors
        nodeGroups.selectAll(".node-rect")
            .classed("dimmed", true)
            .classed("highlighted", false)
            .style("stroke", d => d.color); // Keep original color but dimmed

        // Dim text
        nodeGroups.selectAll(".node-text")
            .style("opacity", 0.2);

        // Dim links while preserving their original colors
        d3.selectAll(".link")
            .classed("dimmed", true)
            .classed("highlighted", false)
            .style("stroke", d => {
                const sourceNode = data.nodes.find(n => n.id === d.source);
                return sourceNode ? sourceNode.color : "#808080";
            });

        // Find all descendants of the clicked node
        const descendants = findDescendants(node.id);
        descendants.add(node.id); // Include the clicked node

        // Highlight relevant nodes
        const highlightedNodes = d3.selectAll(".node-group")
            .filter(d => descendants.has(d.id))
            .classed("dimmed", false);

        // Highlight node rectangles
        highlightedNodes.select(".node-rect")
            .classed("dimmed", false)
            .classed("highlighted", true)
            .style("stroke", d => d.color); // Use original color for highlight

        // Restore text opacity for highlighted nodes
        highlightedNodes.selectAll(".node-text")
            .style("opacity", 1);

        // Highlight relevant edges
        d3.selectAll(".link")
            .filter(d => descendants.has(d.target) && descendants.has(d.source))
            .classed("dimmed", false)
            .classed("highlighted", true)
            .style("stroke", d => {
                const sourceNode = data.nodes.find(n => n.id === d.source);
                return sourceNode ? sourceNode.color : "#808080";
            });

        // Ensure proper layering
        d3.select('.edges').lower();
        d3.select('.nodes').raise();
    }

    // Find all descendant nodes (children and their children)
    function findDescendants(nodeId, descendants = new Set()) {
        const childEdges = data.edges.filter(l => l.source === nodeId);
        childEdges.forEach(link => {
            if (!descendants.has(link.target)) {
                descendants.add(link.target);
                findDescendants(link.target, descendants);
            }
        });
        return descendants;
    }

    // Find all ancestor nodes (parents and their parents)
    function findAncestors(nodeId, ancestors = new Set()) {
        const parentEdges = data.edges.filter(l => l.target === nodeId);
        parentEdges.forEach(link => {
            if (!ancestors.has(link.source)) {
                ancestors.add(link.source);
                findAncestors(link.source, ancestors);
            }
        });
        return ancestors;
    }

    // Clear highlights
    function clearHighlights() {
        // Remove dimming from node groups
        const nodeGroups = d3.selectAll(".node-group")
            .classed("dimmed", false);

        // Reset node rectangles while preserving original colors
        nodeGroups.selectAll(".node-rect")
            .classed("dimmed", false)
            .classed("highlighted", false)
            .style("stroke", d => d.color); // Use the node's own color property

        // Reset text opacity
        nodeGroups.selectAll(".node-text")
            .style("opacity", 1);

        // Reset links while preserving original colors
        d3.selectAll(".link")
            .classed("dimmed", false)
            .classed("highlighted", false)
            .style("stroke", d => {
                // Find the source node for this edge to get its color
                const sourceNode = data.nodes.find(n => n.id === d.source);
                return sourceNode ? sourceNode.color : "#808080"; // Fallback to gray if source not found
            });
    }

    // Context menu functionality
    let menuDiv = null;
    function showContextMenu(evt, d) {
        evt.preventDefault();
        clearMenu();

        // Create menu div with higher z-index
        menuDiv = document.createElement("div");
        menuDiv.className = "context-menu";
        menuDiv.style.zIndex = "9999"; // Ensure menu appears above maximized panel

        // "Select All Parents" for highlight
        const selectParentsItem = document.createElement("div");
        selectParentsItem.className = "context-menu-item";
        selectParentsItem.textContent = "Select All Parents";
        selectParentsItem.onclick = () => {
            highlightNodeWithAncestors(d);
            clearMenu(false);
        };
        menuDiv.appendChild(selectParentsItem);

        // "View Code Block" for the current node
        const viewCodeItem = document.createElement("div");
        viewCodeItem.className = "context-menu-item";
        viewCodeItem.textContent = "View Code Block";
        viewCodeItem.onclick = () => {
            showDialog(d, 'current', globalModuleDocs);
            clearMenu(false);
        };
        menuDiv.appendChild(viewCodeItem);

        // "View Code Blocks (Parents)"
        const viewParentsItem = document.createElement("div");
        viewParentsItem.className = "context-menu-item";
        viewParentsItem.textContent = "View Code Blocks (Parents)";
        viewParentsItem.onclick = () => {
            showDialog(d, 'parents', globalModuleDocs);
            clearMenu(false);
        };
        menuDiv.appendChild(viewParentsItem);

        // "View Code Blocks (Children)"
        const viewChildrenItem = document.createElement("div");
        viewChildrenItem.className = "context-menu-item";
        viewChildrenItem.textContent = "View Code Blocks (Children)";
        viewChildrenItem.onclick = () => {
            showDialog(d, 'children', globalModuleDocs);
            clearMenu(false);
        };
        menuDiv.appendChild(viewChildrenItem);

        // Position the menu at click position
        menuDiv.style.left = evt.pageX + "px";
        menuDiv.style.top = evt.pageY + "px";
        document.body.appendChild(menuDiv);

        highlightNode(d);
    }

    // Highlight node and its ancestors
    function highlightNodeWithAncestors(node) {
        // Reset all to dimmed state
        const nodeGroups = d3.selectAll(".node-group")
            .classed("dimmed", true);

        nodeGroups.selectAll(".node-rect")
            .classed("dimmed", true)
            .classed("highlighted", false)
            .style("stroke", d => d.color);

        nodeGroups.selectAll(".node-text")
            .style("opacity", 0.2);

        d3.selectAll(".link")
            .classed("dimmed", true)
            .classed("highlighted", false)
            .style("stroke", d => {
                const sourceNode = data.nodes.find(n => n.id === d.source);
                return sourceNode ? sourceNode.color : "#808080";
            });

        const ancestors = findAncestors(node.id);
        ancestors.add(node.id); // Include the selected node

        const highlightedNodes = d3.selectAll(".node-group")
            .filter(d => ancestors.has(d.id))
            .classed("dimmed", false);

        highlightedNodes.select(".node-rect")
            .classed("dimmed", false)
            .classed("highlighted", true)
            .style("stroke", d => d.color);

        highlightedNodes.selectAll(".node-text")
            .style("opacity", 1);

        // Highlight edges between ancestors
        d3.selectAll(".link")
            .filter(d => ancestors.has(d.source) && ancestors.has(d.target))
            .classed("dimmed", false)
            .classed("highlighted", true);

        d3.select('.edges').lower();
        d3.select('.nodes').raise();
    }

    // Clear menu
    function clearMenu(shouldClearHighlight = true) {
        if (menuDiv) {
            menuDiv.remove();
            menuDiv = null;
        }
        if (shouldClearHighlight) {
            clearHighlights();
        }
    }

    // Add click listener to clear context menu
    document.addEventListener("click", (e) => {
        if (!e.target.closest(".context-menu") && !e.target.closest(".node-group")) {
            clearMenu();
        }
    });

    // Add zooming capabilities
    const zoom = d3.zoom()
        .scaleExtent([0.2, 3])
        .on("zoom", (evt) => {
            g.attr("transform", evt.transform);
        });
    svg.call(zoom);

    // Calculate initial zoom to fit all nodes
    const anodes = d3.selectAll('.node-group');
    const bounds = {
        left: Infinity,
        right: -Infinity,
        top: Infinity,
        bottom: -Infinity
    };

    anodes.each(function () {
        const bbox = this.getBBox();
        const transform = d3.select(this).attr('transform');
        const translation = transform ? transform.match(/translate\(([-\d.]+),([-\d.]+)\)/) : null;
        const x = translation ? parseFloat(translation[1]) : 0;
        const y = translation ? parseFloat(translation[2]) : 0;

        bounds.left = Math.min(bounds.left, x - bbox.width / 2);
        bounds.right = Math.max(bounds.right, x + bbox.width / 2);
        bounds.top = Math.min(bounds.top, y - bbox.height / 2);
        bounds.bottom = Math.max(bounds.bottom, y + bbox.height / 2);
    });

    if (data.nodes.length === 1 || data.edges.length === 0) {
        const padding = 100;
        bounds.left = -padding;
        bounds.right = padding;
        bounds.top = -padding;
        bounds.bottom = padding;
    }

    // Calculate the scale to fit the content
    const graphWidth = bounds.right - bounds.left;
    const graphHeight = bounds.bottom - bounds.top;
    const scale = Math.min(
        (w - 120) / graphWidth,  // 120px for padding
        (h - 120) / graphHeight
    ) * 0.9;  // 90% of the maximum possible scale for some padding

    // Calculate the center point of the graph
    const cx = (bounds.left + bounds.right) / 2;
    const cy = (bounds.top + bounds.bottom) / 2;

    // Create the initial transform
    const initialTransform = d3.zoomIdentity
        .translate(w / 2 - cx * scale, h / 2 - cy * scale)
        .scale(scale);

    svg.call(zoom.transform, initialTransform);
}

// Dialog for viewing code
function showDialog(node, codeScope = 'current', moduleDocs = {}) {
    // Remove any existing dialogs first
    const existingDialog = document.querySelector('.dialog-overlay2');
    if (existingDialog) {
        existingDialog.remove();
    }

    // Create notification function
    const showNotification = (message, isError = false) => {
        const notification = document.createElement('div');
        notification.style.cssText = `
            position: fixed;
            bottom: 20px;
            right: 20px;
            padding: 12px 24px;
            background: ${isError ? '#ff4444' : '#44b544'};
            color: white;
            border-radius: 4px;
            z-index: 10000;
            font-family: sans-serif;
            box-shadow: 0 2px 8px rgba(0,0,0,0.2);
            transition: opacity 0.5s ease;
        `;
        notification.textContent = message;
        document.body.appendChild(notification);

        setTimeout(() => {
            notification.style.opacity = '0';
            setTimeout(() => notification.remove(), 500);
        }, 3000);
    };

    const dialog = document.createElement('div');
    dialog.className = 'dialog-overlay2';

    const content = document.createElement('div');
    content.className = 'dialog-content2';

    // Set title based on code scope
    const title = document.createElement('div');
    title.className = 'dialog-title2';
    let titleText;
    switch (codeScope) {
        case 'parents':
            titleText = `Parent Code Blocks for ${node.name} (${node.type})`;
            break;
        case 'children':
            titleText = `Child Code Blocks for ${node.name} (${node.type})`;
            break;
        default:
            titleText = `Block Code for ${node.name} (${node.type})`;
    }
    title.textContent = titleText;

    // Create button container for copy buttons
    const buttonContainer = document.createElement('div');
    buttonContainer.className = 'dialog-buttons2';

    // Copy Code button
    const copyCodeBtn = document.createElement('button');
    copyCodeBtn.className = 'dialog-button2';
    copyCodeBtn.innerHTML = '<i class="fas fa-copy"></i> Copy Code';
    copyCodeBtn.onclick = async () => {
        if (codeEditor) {
            const code = codeEditor.getValue();
            try {
                await navigator.clipboard.writeText(code);
                showNotification('Code copied to clipboard');
            } catch (error) {
                console.error('Error copying code:', error);
                showNotification('Error copying code: ' + error.message, true);
            }
        }
    };

    // Copy Code + Modules button
    const copyAllBtn = document.createElement('button');
    copyAllBtn.className = 'dialog-button2';
    copyAllBtn.innerHTML = '<i class="fas fa-copy"></i> Copy Code + Module';
    copyAllBtn.onclick = async () => {
        try {
            const nodeSet = new Set();

            switch (codeScope) {
                case 'parents':
                    findAncestors(node.id).forEach(id => nodeSet.add(id));
                    nodeSet.add(node.id);
                    break;
                case 'children':
                    findDescendants(node.id).forEach(id => nodeSet.add(id));
                    nodeSet.add(node.id);
                    break;
                default:
                    nodeSet.add(node.id);
            }

            const relevantNodes = searchData.nodes.filter(n => nodeSet.has(n.id));
            const moduleSources = new Set(
                relevantNodes
                    .filter(n => n.moduleSrc && n.moduleSrc.trim())
                    .map(n => n.moduleSrc)
            );

            if (moduleSources.size === 0) {
                showNotification('No modules found in selected nodes', true);
                return;
            }

            // Get the current code first
            let combinedDocs = codeEditor.getValue() + '\n\n';

            for (const moduleSource of moduleSources) {
                const moduleDoc = moduleDocs?.[moduleSource]?.ModuleDoc;
                if (moduleDoc) {
                    combinedDocs += `${moduleDoc}\n\n`;
                }
            }

            await navigator.clipboard.writeText(combinedDocs);
            showNotification(`Copied documentation for ${moduleSources.size} module(s)`);

        } catch (error) {
            showNotification('Error copying module documentation: ' + error.message, true);
        }
    };

    buttonContainer.appendChild(copyCodeBtn);
    buttonContainer.appendChild(copyAllBtn);

    const closeBtn = document.createElement('button');
    closeBtn.className = 'dialog-close2';
    closeBtn.innerHTML = '×';

    const code = document.createElement('div');
    code.className = 'dialog-code2';

    content.appendChild(title);
    content.appendChild(buttonContainer);
    content.appendChild(closeBtn);
    content.appendChild(code);
    dialog.appendChild(content);
    document.body.appendChild(dialog);

    let codeEditor = null;

    // Initialize Monaco editor after the dialog is added to DOM
    setTimeout(() => {
        const combinedCode = getCombinedCode(node, codeScope);

        codeEditor = monaco.editor.create(code, {
            value: combinedCode,
            language: 'terraform',
            theme: 'vs-dark',
            minimap: { enabled: false },
            automaticLayout: true,
            readOnly: true,
            lineNumbers: 'off',
            scrollbar: {
                vertical: 'visible',
                horizontal: 'visible'
            },
            fontSize: 14,
            lineDecorationsWidth: 0,
            overviewRulerBorder: false,
            scrollBeyondLastLine: false,
            roundedSelection: false,
            selectOnLineNumbers: true,
            quickSuggestions: false,
            renderLineHighlight: 'none',
            wordBasedSuggestions: false,
            suggest: {
                showIcons: false,
                showStatusBar: false,
                preview: false
            }
        });
    }, 0);

    // Add resize functionality
    let isResizing = false;
    let initialWidth, initialHeight, initialX, initialY;

    const startResize = (e) => {
        const rect = content.getBoundingClientRect();
        const isInResizeArea =
            e.clientX > rect.right - 15 &&
            e.clientY > rect.bottom - 15;

        if (!isInResizeArea) return;

        isResizing = true;
        initialWidth = content.offsetWidth;
        initialHeight = content.offsetHeight;
        initialX = e.clientX;
        initialY = e.clientY;

        content.classList.add('resizing');
        e.preventDefault();
    };

    const stopResize = () => {
        isResizing = false;
        content.classList.remove('resizing');
    };

    const resize = (e) => {
        if (!isResizing) return;

        const width = initialWidth + (e.clientX - initialX);
        const height = initialHeight + (e.clientY - initialY);

        content.style.width = `${Math.max(300, width)}px`;
        content.style.height = `${Math.max(200, height)}px`;

        if (codeEditor) {
            codeEditor.layout();
        }
    };

    // Cleanup function for event listeners and editor
    const cleanup = () => {
        document.removeEventListener('mousemove', resize);
        document.removeEventListener('mouseup', stopResize);
        if (codeEditor) {
            codeEditor.dispose();
        }
    };

    // Add event listeners for resizing
    content.addEventListener('mousedown', startResize);
    document.addEventListener('mousemove', resize);
    document.addEventListener('mouseup', stopResize);

    // Handle dialog close button
    closeBtn.addEventListener('click', (e) => {
        e.preventDefault();
        e.stopPropagation();
        cleanup();
        dialog.remove();
    });

    // Handle clicking outside the dialog
    dialog.addEventListener('click', (e) => {
        if (e.target === dialog) {
            cleanup();
            dialog.remove();
        }
    });

    // Add escape key handler
    const handleEscape = (e) => {
        if (e.key === 'Escape') {
            cleanup();
            dialog.remove();
            document.removeEventListener('keydown', handleEscape);
        }
    };
    document.addEventListener('keydown', handleEscape);
}

// Helper function to get combined code based on scope
function getCombinedCode(node, scope) {
    let nodeSet = new Set();
    console.log('Getting combined code for node:', node);

    switch (scope) {
        case 'parents':
            nodeSet = findAncestors(node.id);
            nodeSet.add(node.id); // also include the clicked node
            break;
        case 'children':
            nodeSet = findDescendants(node.id);
            nodeSet.add(node.id);
            break;
        default:
            nodeSet.add(node.id);
    }

    const relevantNodes = searchData.nodes.filter(n => nodeSet.has(n.id));
    return relevantNodes
        .map(n => n.codeBlock || '# No code block available')
        .join('\n---------------------\n');
}

// Function to search nodes
function searchNodes(searchTerm) {
    // Reset previous highlights
    clearHighlights();

    if (!searchTerm) {
        currentSearchResults = [];
        currentSearchIndex = -1;
        $('#searchCount').text('');
        return;
    }

    // Find matching nodes
    currentSearchResults = searchData.nodes.filter(node =>
        node.name.toLowerCase().includes(searchTerm.toLowerCase())
    );

    if (currentSearchResults.length > 0) {
        currentSearchIndex = 0;
        highlightSearchResults();
    } else {
        $('#searchCount').text('No matches');
    }
}

// Function to highlight search results
function highlightSearchResults() {
    // First, dim all nodes and remove all highlights
    d3.selectAll('.node-group')
        .classed('dimmed', true);

    d3.selectAll('.node-rect')
        .classed('dimmed', true)
        .classed('highlighted', false)
        .style('stroke-width', '2px')
        .style('stroke', d => d.color);  // Restore original colors

    d3.selectAll('.node-text')
        .style('opacity', 0.2);

    // Get the current node
    const currentNode = currentSearchResults[currentSearchIndex];
    if (!currentNode) return;

    // Find and highlight only the current node
    const nodeElement = d3.selectAll('.node-group')
        .filter(d => d.id === currentNode.id);

    // Remove dimming and add highlight to the current node
    nodeElement
        .classed('dimmed', false);

    nodeElement.select('.node-rect')
        .classed('dimmed', false)
        .classed('highlighted', true)
        .style('stroke-width', '4px')
        .style('stroke', currentNode.color)  // Ensure color is maintained
        .style('filter', 'drop-shadow(0 0 12px rgba(255, 255, 255, 0.3))');  // Add glow effect

    nodeElement.selectAll('.node-text')
        .style('opacity', 1);

    // Update counter
    $('#searchCount').text(`${currentSearchIndex + 1} of ${currentSearchResults.length}`);

    // Zoom to current node with increased zoom factor
    const svg = d3.select("#orgChart");
    const targetScale = 2.0;
    const x = -currentNode.x * targetScale + svg.node().clientWidth / 2;
    const y = -currentNode.y * targetScale + svg.node().clientHeight / 2;

    svg.transition()
        .duration(750)
        .call(
            d3.zoom().transform,
            d3.zoomIdentity
                .translate(x, y)
                .scale(targetScale)
        );
}

// Search functionality
let currentSearchResults = [];
let currentSearchIndex = -1;
let searchData = null;

function setupSearch() {
    // Set reference to graph data for searching
    searchData = data;

    $('#nodeSearch').on('input', function () {
        searchNodes(this.value);
    });

    $('#searchNext').on('click', function () {
        if (currentSearchResults.length === 0) return;
        currentSearchIndex = (currentSearchIndex + 1) % currentSearchResults.length;
        highlightSearchResults();
    });

    $('#searchPrev').on('click', function () {
        if (currentSearchResults.length === 0) return;
        currentSearchIndex = currentSearchIndex <= 0 ?
            currentSearchResults.length - 1 :
            currentSearchIndex - 1;
        highlightSearchResults();
    });

    // Add event listeners for keyboard navigation in search
    $(document).on('keydown', function (e) {
        if (document.activeElement === $('#nodeSearch')[0]) {
            if (e.key === 'Enter') {
                e.preventDefault();
                if (e.shiftKey) {
                    $('#searchPrev').click();
                } else {
                    $('#searchNext').click();
                }
            }
        }
    });
}
    // Export additional functions for Blazor interop
    export function updateGraphData(newData, dotNetRef) {
        // Update the searchData reference for searches
        searchData = newData;
        dotNetHelper = dotNetRef;

        // Find the container and re-render the graph
        const container = document.querySelector('.graph-container');
        if (container) {
            // Clear existing graph
            const svg = d3.select("#orgChart");
            svg.selectAll("*").remove();

            // Re-render with new data
            renderGraph(newData, container);
            setupSearch();
        }
    }

    // Function to clear the current graph
    export function clearGraph() {
        const svg = d3.select("#orgChart");
        if (!svg.empty()) {
            svg.selectAll("*").remove();
        }

        // Reset search state
        currentSearchResults = [];
        currentSearchIndex = -1;
        $('#searchCount').text('');
        $('#nodeSearch').val('');
    }

    // Function to highlight a specific node by ID
    export function highlightNodeById(nodeId) {
        if (!searchData) return;

        const node = searchData.nodes.find(n => n.id === parseInt(nodeId));
        if (node) {
            highlightNode(node);

            // Center the view on this node
            const svg = d3.select("#orgChart");
            const targetScale = 2.0;
            const x = -node.x * targetScale + svg.node().clientWidth / 2;
            const y = -node.y * targetScale + svg.node().clientHeight / 2;

            svg.transition()
                .duration(750)
                .call(
                    d3.zoom().transform,
                    d3.zoomIdentity
                        .translate(x, y)
                        .scale(targetScale)
                );

            return true;
        }
        return false;
    }

    // Function to export the current view as SVG
    export function exportSvg() {
        const svg = document.getElementById('orgChart');
        if (!svg) return null;

        // Clone the SVG to avoid modifying the displayed one
        const clone = svg.cloneNode(true);

        // Set width and height attributes explicitly
        clone.setAttribute('width', svg.clientWidth);
        clone.setAttribute('height', svg.clientHeight);

        // Add CSS styles as a stylesheet within the SVG
        const style = document.createElement('style');
        style.textContent = `
        .node-rect { fill: #2a2a2a; stroke-width: 2px; rx: 8; ry: 8; }
        .node-rect.highlighted { stroke-width: 3px; filter: drop-shadow(0 0 12px rgba(255, 255, 255, 0.3)); }
        .node-text { fill: #fff; font-size: 12px; }
        .link { fill: none; stroke-width: 2px; }
        .link.highlighted { stroke-width: 3px; }
    `;
        clone.insertBefore(style, clone.firstChild);

        // Serialize to string
        const serializer = new XMLSerializer();
        const svgString = serializer.serializeToString(clone);

        // Create a Blob and URL
        const blob = new Blob([svgString], { type: 'image/svg+xml' });
        return URL.createObjectURL(blob);
    }

    // Helper function to get node type icon
    function getNodeTypeIcon(type) {
        switch (type.toLowerCase()) {
            case 'variable': return 'fa-code-branch';
            case 'resource': return 'fa-cube';
            case 'data': return 'fa-database';
            case 'module': return 'fa-puzzle-piece';
            case 'output': return 'fa-sign-out-alt';
            case 'local': return 'fa-cog';
            default: return 'fa-file-code';
        }
    }

    // Helper function to format code for the Monaco editor
    function formatCodeForEditor(code) {
        if (!code) return '# No code available';

        // Remove excessive blank lines
        return code.replace(/\n{3,}/g, '\n\n');
    }

    // Function to handle window resizing
    function handleResize() {
        const container = document.querySelector('.graph-container');
        const svg = d3.select("#orgChart");

        if (container && !svg.empty()) {
            svg.attr("width", container.clientWidth)
                .attr("height", container.clientHeight);
        }
    }

    // Add window resize handler
    window.addEventListener('resize', handleResize);

    // Allow registering callback for node clicks
    export function registerNodeClickCallback(callback) {
        window.nodeClickCallback = callback;
    }

    // Clean up function to remove event listeners
    export function cleanup() {
        window.removeEventListener('resize', handleResize);
        document.removeEventListener('click', clearMenu);
        $(document).off('keydown');
        $('#nodeSearch').off('input');
        $('#searchNext').off('click');
        $('#searchPrev').off('click');
    }