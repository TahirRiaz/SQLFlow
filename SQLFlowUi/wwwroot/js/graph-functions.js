"use strict";

/**
 * SQLFlowGraph - A visualization library for SQL dependency graphs
 * This library provides functionality to render, interact with, and manipulate
 * directed graphs representing SQL dependencies.
 */
window.SQLFlowGraph = {};

/******************************************************************************
 * 1) INITIALIZATION: Setup container and global styles
 ******************************************************************************/
window.initializeGraph = function (container) {
    console.log("Initializing graph container:", container);

    if (!window.d3) {
        console.error("D3 library not available");
        return false;
    }

    // Resolve the container element (DOM node) from various possible inputs
    let containerElement = resolveContainerElement(container);
    if (!containerElement) {
        console.warn("Container element not found, using document.body as fallback");
        containerElement = document.body;
    }

    console.log("Final container element resolved:", containerElement);

    // Ensure an <svg id="orgChart"> is present
    let svg = containerElement.querySelector("svg#orgChart");
    if (!svg) {
        svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
        svg.id = "orgChart";
        svg.setAttribute("width", "100%");
        svg.setAttribute("height", "100%");
        containerElement.appendChild(svg);
        console.log("Created new SVG element:", svg);
    } else {
        console.log("Using existing SVG element:", svg);
    }

    // Add the main graph CSS styles once
    addGraphStyles();

    // Return success
    return true;
};

/******************************************************************************
 * 2) DATA RENDERING: Parse JSON and render graph
 ******************************************************************************/
window.renderGraphFromJson = function (jsonString, container) {
    console.log("Rendering graph from JSON");
    if (!window.d3) {
        console.error("D3 library not available");
        return false;
    }

    try {
        const data = JSON.parse(jsonString);
        if (!data.nodes || !data.edges) {
            console.error("Invalid data structure: expected { nodes, edges }");
            return false;
        }

        let containerElement = resolveContainerElement(container);
        if (!containerElement) {
            console.warn("Container element not found, using body fallback");
            containerElement = document.body;
        }

        // Render the graph
        window.SQLFlowGraph.renderGraph(data, containerElement);

        return true;
    } catch (error) {
        console.error("Error rendering graph from JSON:", error);
        return false;
    }
};

/******************************************************************************
 * 3) DIALOG RENDERING: Show graph in a modal dialog
 ******************************************************************************/
window.showGraphDialog = function (jsonString) {
    // Create a modal overlay
    const modal = document.createElement("div");
    modal.className = "graph-modal-overlay";
    modal.style.cssText = `
        position: fixed; top: 0; left: 0; right: 0; bottom: 0;
        background-color: rgba(0, 0, 0, 0.8);
        z-index: 9999; display: flex; justify-content: center; align-items: center;
    `;

    // Create the modal content
    const content = document.createElement("div");
    content.className = "graph-modal-content";
    content.style.cssText = `
        width: 90%; height: 90%; background-color: #1e1e1e; border-radius: 8px;
        display: flex; flex-direction: column; overflow: hidden; position: relative;
    `;

    // Header with a close button
    const header = document.createElement("div");
    header.style.cssText = `
        display: flex; justify-content: space-between; align-items: center;
        padding: 16px; background-color: #2d2d2d; border-bottom: 1px solid #444;
    `;
    const title = document.createElement("h3");
    title.textContent = "SQLFlow Dependency Graph";
    title.style.cssText = "margin: 0; color: white; font-size: 18px;";

    const closeButton = document.createElement("button");
    closeButton.textContent = "×";
    closeButton.style.cssText = `
        background: none; border: none; color: white; font-size: 24px;
        cursor: pointer; padding: 0; margin: 0;
    `;
    closeButton.onclick = () => modal.remove();

    header.appendChild(title);
    header.appendChild(closeButton);

    // Graph container inside the dialog
    const graphContainer = document.createElement("div");
    graphContainer.style.cssText = `
        flex: 1; position: relative; overflow: hidden;
    `;
    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.id = "modalOrgChart";
    svg.style.cssText = "width: 100%; height: 100%;";
    graphContainer.appendChild(svg);

    // Build the modal structure
    content.appendChild(header);
    content.appendChild(graphContainer);
    modal.appendChild(content);
    document.body.appendChild(modal);

    // Render the graph inside the modal
    try {
        const data = JSON.parse(jsonString);
        // We give a special container ID or element
        window.SQLFlowGraph.renderGraph(data, graphContainer, "modalOrgChart");
    } catch (error) {
        console.error("Error rendering graph in modal:", error);
    }
};

/******************************************************************************
 * 4) EXPORT: Download current graph as PNG
 ******************************************************************************/
window.downloadGraphAsPng = function (filename) {
    const svg = document.getElementById("orgChart");
    if (!svg) {
        console.error("SVG with id='orgChart' not found");
        return;
    }

    try {
        // Clone the SVG
        const clone = svg.cloneNode(true);
        const svgData = new XMLSerializer().serializeToString(clone);

        // Insert background rect so it's not transparent
        const parser = new DOMParser();
        const svgDoc = parser.parseFromString(svgData, "image/svg+xml");
        const svgRoot = svgDoc.documentElement;

        const bgRect = document.createElementNS("http://www.w3.org/2000/svg", "rect");
        bgRect.setAttribute("width", "100%");
        bgRect.setAttribute("height", "100%");
        bgRect.setAttribute("fill", "#0F1C2E");

        if (svgRoot.firstChild) {
            svgRoot.insertBefore(bgRect, svgRoot.firstChild);
        } else {
            svgRoot.appendChild(bgRect);
        }

        const svgWithBg = new XMLSerializer().serializeToString(svgDoc);

        // Create a canvas
        const canvas = document.createElement("canvas");
        const ctx = canvas.getContext("2d");
        const img = new Image();

        img.onload = function () {
            canvas.width = img.width;
            canvas.height = img.height;
            ctx.drawImage(img, 0, 0);

            const link = document.createElement("a");
            link.download = filename || "graph.png";
            link.href = canvas.toDataURL("image/png");
            link.click();
        };

        // Ensure UTF-8 is encoded properly
        img.src = "data:image/svg+xml;base64," + btoa(unescape(encodeURIComponent(svgWithBg)));
    } catch (error) {
        console.error("Error downloading graph:", error);
    }
};

/******************************************************************************
 * 5) ZOOM CONTROLS: Functions for manipulating view
 ******************************************************************************/
window.zoomIn = function () {
    const svg = d3.select("#orgChart");
    const currentZoom = d3.zoomTransform(svg.node());
    const newScale = currentZoom.k * 1.3; // Increase scale by 30%

    svg.transition()
        .duration(300)
        .call(
            d3.zoom().transform,
            d3.zoomIdentity
                .translate(currentZoom.x, currentZoom.y)
                .scale(newScale)
        );
};

window.zoomOut = function () {
    const svg = d3.select("#orgChart");
    const currentZoom = d3.zoomTransform(svg.node());
    const newScale = currentZoom.k * 0.7; // Decrease scale by 30%

    svg.transition()
        .duration(300)
        .call(
            d3.zoom().transform,
            d3.zoomIdentity
                .translate(currentZoom.x, currentZoom.y)
                .scale(newScale)
        );
};

window.resetZoom = function () {
    const svg = d3.select("#orgChart");
    const g = svg.select("g");

    if (g.empty()) return;

    const containerElement = svg.node().parentElement;
    if (!containerElement) return;

    const width = containerElement.clientWidth || 800;
    const height = containerElement.clientHeight || 600;

    // Get bounding box of all drawn elements
    const bbox = g.node().getBBox();
    if (!bbox.width || !bbox.height) return;

    // Add a little padding
    const scale = 0.9 * Math.min((width - 120) / bbox.width, (height - 120) / bbox.height);

    // Center the graph
    const tx = width / 2 - scale * (bbox.x + bbox.width / 2);
    const ty = height / 2 - scale * (bbox.y + bbox.height / 2);

    const initialTransform = d3.zoomIdentity.translate(tx, ty).scale(scale);
    svg.transition().duration(750).call(d3.zoom().transform, initialTransform);
};

/******************************************************************************
 * 6) GRAPH RENDERING: Core graph visualization logic
 ******************************************************************************/
/**
 * Main render function: draws the graph using D3, sets up zoom, highlight, etc.
 * @param {Object} data - graph data with nodes and edges
 * @param {Element} containerElement - DOM container in which to render
 * @param {string} [svgId="orgChart"] - optional custom svg ID
 */
window.SQLFlowGraph.renderGraph = function (data, containerElement, svgId = "orgChart") {
    console.log("SQLFlowGraph.renderGraph called with data:", data);
    if (!data || !data.nodes || !data.edges) {
        console.error("Data must have { nodes, edges }");
        return;
    }

    // Store data globally so highlight functions can access it
    window.SQLFlowGraph._graphData = data;

    // Ensure the container has an <svg> with the given svgId
    let svgSel = d3.select(containerElement).select(`#${svgId}`);
    if (svgSel.empty()) {
        svgSel = d3.select(containerElement)
            .append("svg")
            .attr("id", svgId)
            .attr("width", "100%")
            .attr("height", "100%");
    }
    // Clear any previous content
    svgSel.selectAll("*").remove();

    const svg = svgSel;
    // Add a main <g> to hold nodes & edges
    const g = svg.append("g");

    // Temporarily append an invisible group to measure text
    const measureGroup = svg.append("g").style("visibility", "hidden");
    function getNodeDimensions(d) {
        measureGroup.selectAll("*").remove();
        const nameTxt = measureGroup.append("text").text(d.label || d.name || "Unnamed");
        const projectTxt = measureGroup.append("text").text(d.type || "Unknown");

        const nameBox = nameTxt.node().getBBox();
        const projBox = projectTxt.node().getBBox();

        const w = Math.max(nameBox.width, projBox.width) + 40;
        // You can tweak this height if desired
        const h = 46;
        return { width: w, height: h };
    }

    // Enrich nodes with ids, labels, default color, and measured dimensions
    data.nodes.forEach((n) => {
        n.id = n.id || `node_${Math.random().toString(36).slice(2)}`;
        n.label = n.label || n.name || "Unnamed";
        n.type = n.type || "Unknown";
        n.color = n.color || "#4285F4";
        n.dimensions = getNodeDimensions(n);
    });

    measureGroup.remove();

    // Position nodes by (node.level) if present, or level=0
    // This basic approach lumps nodes on discrete levels.
    function calculatePositions() {
        const levelGroups = {};
        data.nodes.forEach((node) => {
            const lvl = node.level || 0;
            if (!levelGroups[lvl]) levelGroups[lvl] = [];
            levelGroups[lvl].push(node);
        });

        const minSpacing = 30;
        const levelHeight = 120;
        for (const [lvlStr, nodes] of Object.entries(levelGroups)) {
            const lvl = parseInt(lvlStr, 10);
            const totalWidth = nodes.reduce(
                (sum, nd, i) => sum + nd.dimensions.width + (i < nodes.length - 1 ? minSpacing : 0),
                0
            );
            let currentX = -totalWidth / 2;
            nodes.forEach((nd) => {
                nd.x = currentX + nd.dimensions.width / 2;
                nd.y = lvl * levelHeight;
                currentX += nd.dimensions.width + minSpacing;
            });
        }
    }
    calculatePositions();

    // Add edges group
    const edgesG = g.append("g").attr("class", "edges");
    const edgesSel = edgesG
        .selectAll(".link")
        .data(data.edges)
        .join("path")
        .attr("class", "link")
        .style("stroke", (d) => {
            const src = data.nodes.find((n) => n.id === d.source);
            return src?.color || "#808080";
        });

    // Add nodes group
    const nodesG = g.append("g").attr("class", "nodes");
    const nodeGroups = nodesG
        .selectAll(".node-group")
        .data(data.nodes)
        .join("g")
        .attr("class", "node-group")
        .attr("transform", (d) => `translate(${d.x},${d.y})`)
        .on("click", (event, d) => {
            event.stopPropagation();
            window.SQLFlowGraph.highlightNodeById(d.id);
        });

    // Draw node rectangles & text
    nodeGroups.each(function (d) {
        const ng = d3.select(this);
        ng.append("rect")
            .attr("class", "node-rect")
            .attr("width", d.dimensions.width)
            .attr("height", d.dimensions.height)
            .attr("x", -d.dimensions.width / 2)
            .attr("y", -20)
            .attr("rx", 4)
            .attr("ry", 4)
            .style("stroke", d.color)
            .style("fill", d3.color(d.color).copy({ opacity: 0.3 }));

        ng.append("text")
            .attr("class", "node-text project-text")
            .attr("text-anchor", "middle")
            .attr("y", -3)
            .style("font-size", "11px")
            .text((d) => d.type + (d.batch ? ` (${d.batch})` : ""));

        ng.append("text")
            .attr("class", "node-text name-text")
            .attr("y", 12)
            .attr("text-anchor", "middle")
            .style("font-size", "13px")
            .style("font-weight", "bold")
            .text(d.label);

        // Optionally, add a small status circle
        if (typeof d.status !== "undefined") {
            const statusColor =
                d.status === 1 ? "#4CAF50" :
                    d.status === 0 ? "#F44336" : "#FFC107";

            ng.append("circle")
                .attr("r", 5)
                .attr("cx", -d.dimensions.width / 2 + 15)
                .attr("cy", 0)
                .attr("fill", statusColor);
        }

        // Tooltip
        ng.append("title").text(d.description || d.tooltip || d.label || "Unnamed");
    });

    // Generate the edges as curved lines
    const lineGen = d3.line().curve(d3.curveBasis);
    edgesSel.attr("d", (edge) => {
        const s = data.nodes.find((n) => n.id === edge.source);
        const t = data.nodes.find((n) => n.id === edge.target);
        if (!s || !t) return "";
        const midY = (s.y + t.y) / 2;
        return lineGen([
            [s.x, s.y],
            [s.x, s.y + 20],
            [s.x, midY],
            [t.x, midY],
            [t.x, t.y - 20],
            [t.x, t.y],
        ]);
    });

    // Zoom support
    const zoom = d3.zoom()
        .scaleExtent([0.2, 3])
        .on("zoom", (ev) => g.attr("transform", ev.transform));
    svg.call(zoom);

    // Auto-zoom to fit the content
    window.resetZoom();

    // Basic (optional) search setup if #nodeSearch etc. exist in DOM
    setupSearch();

    // Click outside to clear highlights
    svg.on("click", function (event) {
        if (event.target === this) {
            window.SQLFlowGraph.clearHighlights();
        }
    });

    // Simple search mechanism
    function setupSearch() {
        const searchInput = document.getElementById("nodeSearch");
        const prevBtn = document.getElementById("searchPrev");
        const nextBtn = document.getElementById("searchNext");
        const countLabel = document.getElementById("searchCount");

        if (!searchInput || !prevBtn || !nextBtn || !countLabel) {
            return;
        }

        let results = [];
        let currentIndex = -1;

        searchInput.addEventListener("input", () => {
            window.SQLFlowGraph.clearHighlights();
            const term = searchInput.value.trim().toLowerCase();
            if (!term) {
                results = [];
                currentIndex = -1;
                countLabel.textContent = "";
                return;
            }
            results = data.nodes.filter((nd) => {
                const lbl = nd.label?.toLowerCase() || "";
                const nm = nd.name?.toLowerCase() || "";
                const tp = nd.type?.toLowerCase() || "";
                return lbl.includes(term) || nm.includes(term) || tp.includes(term);
            });
            if (results.length) {
                currentIndex = 0;
                doHighlight();
            } else {
                countLabel.textContent = "No matches";
            }
        });

        prevBtn.addEventListener("click", () => {
            if (!results.length) return;
            currentIndex = currentIndex <= 0 ? results.length - 1 : currentIndex - 1;
            doHighlight();
        });

        nextBtn.addEventListener("click", () => {
            if (!results.length) return;
            currentIndex = (currentIndex + 1) % results.length;
            doHighlight();
        });

        function doHighlight() {
            // Clear everything first
            d3.selectAll(".node-group").classed("dimmed", true);
            d3.selectAll(".node-rect").classed("dimmed", true).classed("highlighted", false);
            d3.selectAll(".node-text").style("opacity", 0.2);

            const node = results[currentIndex];
            // Highlight the matching node
            d3.selectAll(".node-group")
                .filter((d) => d.id === node.id)
                .classed("dimmed", false)
                .select(".node-rect")
                .classed("dimmed", false)
                .classed("highlighted", true);

            d3.selectAll(".node-group")
                .filter((d) => d.id === node.id)
                .selectAll(".node-text")
                .style("opacity", 1);

            countLabel.textContent = `${currentIndex + 1} of ${results.length}`;

            // Optionally zoom/center on the matched node
            const nodeEl = d3.selectAll(".node-group").filter((d) => d.id === node.id).node();
            if (nodeEl) {
                const containerElement = svg.node().parentElement;
                const width = containerElement.clientWidth || 800;
                const height = containerElement.clientHeight || 600;

                const transform = nodeEl.getAttribute("transform");
                const match = /translate\(([-\d.]+),\s*([-\d.]+)\)/.exec(transform);
                if (match) {
                    const x = parseFloat(match[1]);
                    const y = parseFloat(match[2]);
                    const scale = 1.5;
                    svg.transition()
                        .duration(750)
                        .call(
                            zoom.transform,
                            d3.zoomIdentity
                                .translate(width / 2 - x * scale, height / 2 - y * scale)
                                .scale(scale)
                        );
                }
            }
        }
    }
};

/******************************************************************************
 * 7) HIGHLIGHT FUNCTIONS: Node and edge highlighting
 ******************************************************************************/
/**
 * Clear all highlighting from the graph
 */
window.SQLFlowGraph.clearHighlights = function () {
    try {
        d3.selectAll(".node-group").classed("dimmed", false);
        d3.selectAll(".node-rect").classed("dimmed", false).classed("highlighted", false);
        d3.selectAll(".node-text").style("opacity", 1);
        d3.selectAll(".link").classed("dimmed", false).classed("highlighted", false);
    } catch (error) {
        console.error("Error in clearHighlights:", error);
    }
};

/**
 * Highlight a node and all its ancestors by ID. Will dim all other nodes and edges.
 * @param {string} nodeId - ID of the node to highlight
 */
window.SQLFlowGraph.highlightNodeWithAncestorsById = function (nodeId) {
    const data = window.SQLFlowGraph._graphData;
    if (!data) {
        console.error("No graph data in SQLFlowGraph._graphData");
        return;
    }

    const node = data.nodes.find((n) => n.id === nodeId);
    if (!node) {
        console.warn("Node not found for ID:", nodeId);
        return;
    }

    // Clear existing highlights
    window.SQLFlowGraph.clearHighlights();

    // Find all ancestors
    const ancestors = new Set();
    findAncestors(nodeId, ancestors);

    // Add the current node to the set
    ancestors.add(nodeId);

    // Apply highlighting
    d3.selectAll(".node-group").classed("dimmed", (d) => !ancestors.has(d.id));
    d3.selectAll(".node-rect")
        .classed("dimmed", (d) => !ancestors.has(d.id))
        .classed("highlighted", (d) => d.id === nodeId);

    d3.selectAll(".node-text").style("opacity", (d) =>
        ancestors.has(d.id) ? 1 : 0.2
    );

    // Highlight edges where both source and target are in ancestors
    d3.selectAll(".link")
        .classed("dimmed", (d) => {
            return !(ancestors.has(d.source) && ancestors.has(d.target));
        })
        .classed("highlighted", (d) => {
            return ancestors.has(d.source) && ancestors.has(d.target);
        });

    // Keep nodes above edges
    const edgesG = d3.select(".edges");
    const nodesG = d3.select(".nodes");
    if (!edgesG.empty()) edgesG.lower();
    if (!nodesG.empty()) nodesG.raise();

    // Recursive helper
    function findAncestors(targetId, visited) {
        const parentEdges = data.edges.filter(e => e.target === targetId);
        for (const edge of parentEdges) {
            const sourceId = edge.source;
            if (!visited.has(sourceId)) {
                visited.add(sourceId);
                findAncestors(sourceId, visited);
            }
        }
    }
};

/******************************************************************************
 * 8) CODE DIALOG: Display code associated with nodes
 ******************************************************************************/
window.SQLFlowGraph.showCodeDialogById = function (nodeId, scope = "current") {
    const data = window.SQLFlowGraph._graphData;
    if (!data) {
        console.error("No graph data in SQLFlowGraph._graphData");
        return;
    }
    const node = data.nodes.find((n) => n.id === nodeId);
    if (!node) {
        console.warn("Node not found for ID:", nodeId);
        return;
    }

    // Remove existing dialog if present
    const existing = document.querySelector(".dialog-overlay-code");
    if (existing) existing.remove();

    // Create dialog overlay
    const overlay = document.createElement("div");
    overlay.className = "dialog-overlay-code";
    overlay.style.cssText = `
        position: fixed; top: 0; left: 0; right: 0; bottom: 0;
        background-color: rgba(0, 0, 0, 0.75);
        display: flex; justify-content: center; align-items: center;
        z-index: 1001; backdrop-filter: blur(3px);
    `;

    // Create dialog body
    const dialog = document.createElement("div");
    dialog.style.cssText = `
        background: #333; padding: 24px; border-radius: 8px;
        width: 80%; max-width: 800px; height: 70vh; position: relative;
        color: #fff; box-shadow: 0 8px 32px rgba(0,0,0,0.3);
        overflow: hidden; display: flex; flex-direction: column;
    `;

    // Dialog title
    const title = document.createElement("h3");
    title.style.margin = "0 0 12px 0";
    let titleText = `Block Code for ${node.label} (${node.type})`;
    if (scope === "parents") {
        titleText = `Parent Code Blocks for ${node.label} (${node.type})`;
    } else if (scope === "children") {
        titleText = `Child Code Blocks for ${node.label} (${node.type})`;
    }
    title.textContent = titleText;

    // Code content
    const pre = document.createElement("pre");
    pre.style.flex = "1";
    pre.style.overflow = "auto";
    pre.style.background = "#1e1e1e";
    pre.style.padding = "12px";
    pre.style.borderRadius = "4px";

    // Gather relevant nodes based on scope
    let nodeSet = new Set();
    if (scope === "parents") {
        findAncestors(nodeId, nodeSet);
        nodeSet.add(nodeId);
    } else if (scope === "children") {
        findDescendants(nodeId, nodeSet);
        nodeSet.add(nodeId);
    } else {
        nodeSet.add(nodeId);
    }

    const relevant = data.nodes.filter((n) => nodeSet.has(n.id));
    const codeText = relevant
        .map((n) => {
            const header = `/* ${n.label} (${n.type}) */\n`;
            return header + (n.codeBlock || "# No code block available");
        })
        .join("\n\n---------------------\n\n");

    pre.textContent = codeText;

    // Close button
    const closeBtn = document.createElement("button");
    closeBtn.textContent = "×";
    closeBtn.style.cssText = `
        position: absolute; top: 8px; right: 8px; background: none;
        border: none; color: #fff; font-size: 24px; cursor: pointer;
    `;
    closeBtn.onclick = () => overlay.remove();

    // Assemble dialog
    dialog.appendChild(title);
    dialog.appendChild(pre);
    dialog.appendChild(closeBtn);
    overlay.appendChild(dialog);
    document.body.appendChild(overlay);

    // Helper functions for finding related nodes
    function findDescendants(sourceId, visited) {
        const childEdges = data.edges.filter(e => e.source === sourceId);
        for (const edge of childEdges) {
            const targetId = edge.target;
            if (!visited.has(targetId)) {
                visited.add(targetId);
                findDescendants(targetId, visited);
            }
        }
    }

    function findAncestors(targetId, visited) {
        const parentEdges = data.edges.filter(e => e.target === targetId);
        for (const edge of parentEdges) {
            const sourceId = edge.source;
            if (!visited.has(sourceId)) {
                visited.add(sourceId);
                findAncestors(sourceId, visited);
            }
        }
    }
};

/******************************************************************************
 * 9) HELPER FUNCTIONS: DOM manipulation and utilities
 ******************************************************************************/
/**
 * Resolve a container element from various possible inputs.
 * If invalid, defaults to document.body.
 * @param {string|Element} container - Container reference (selector, element, etc.)
 * @returns {Element|null} - DOM element or null if not found
 */
function resolveContainerElement(container) {
    if (!container) return document.body;

    // 1) If string, treat as a CSS selector
    if (typeof container === "string") {
        const el = document.querySelector(container);
        if (el) return el;
    }

    // 2) If already a DOM element
    if (container instanceof Element) {
        return container;
    }

    // Fallback
    return document.body;
}

/**
 * Add graph styles to the document if not already present
 */
function addGraphStyles() {
    let styleEl = document.getElementById("graphStyles");
    if (!styleEl) {
        styleEl = document.createElement("style");
        styleEl.id = "graphStyles";
        document.head.appendChild(styleEl);
    }

    const css = `
/* Main node/link styling */
.node-group {
    cursor: pointer;
    transition: opacity 0.3s;
}
.node-rect {
    fill: #2d2d2d;
    rx: 4;
    ry: 4;
    stroke-width: 2px;
    transition: all 0.3s;
}
.node-text {
    font-family: Arial, sans-serif;
    fill: #ffffff;
    text-anchor: middle;
    transition: opacity 0.3s;
}
.project-text {
    font-size: 11px;
    opacity: 0.8;
}
.name-text {
    font-size: 13px;
    font-weight: bold;
}
.link {
    fill: none;
    stroke-width: 2px;
    opacity: 0.7;
    transition: all 0.3s;
}
/* Highlight/dim styles */
.node-group.dimmed { opacity: 0.3; }
.node-rect.dimmed { opacity: 0.3; }
.node-rect.highlighted {
    stroke-width: 4px;
    filter: drop-shadow(0 0 5px rgba(255, 255, 255, 0.5));
}
.link.dimmed { opacity: 0.1; }
.link.highlighted {
    stroke-width: 3px;
    opacity: 1;
}
/* Modal overlay (for showGraphDialog) */
.graph-modal-overlay {
    position: fixed; top: 0; left: 0; right: 0; bottom: 0;
    background-color: rgba(0, 0, 0, 0.8);
    z-index: 9999;
    display: flex; justify-content: center; align-items: center;
}
/* Code dialog overlay (for showCodeDialogById) */
.dialog-overlay-code {
    position: fixed; top: 0; left: 0; right: 0; bottom: 0;
    background-color: rgba(0, 0, 0, 0.75);
    display: flex; justify-content: center; align-items: center;
    z-index: 1001; backdrop-filter: blur(3px);
}
`;

    styleEl.textContent = css;
}

/******************************************************************************
 * ADDITIONAL BRIDGING FUNCTIONS (Blazor or External) 
 ******************************************************************************/
/**
 * Bridge function to highlight a node from external code (e.g. Blazor).
 * @param {string} nodeId - ID of the node to highlight
 * @returns {boolean} - Success indicator
 */
window.highlightNodeFromBlazor = function (nodeId) {
    console.log("Highlighting node from Blazor/external call:", nodeId);
    if (window.SQLFlowGraph && typeof window.SQLFlowGraph.highlightNodeById === 'function') {
        window.SQLFlowGraph.highlightNodeById(nodeId);
        return true;
    } else {
        console.error("SQLFlowGraph or highlightNodeById function not available");
        return false;
    }
};

/**
 * Highlight a node and its descendants by ID (main entry for node selection).
 * @param {string} nodeId - ID of the node to highlight
 */
window.SQLFlowGraph.highlightNodeById = function (nodeId) {
    console.log("Highlighting node:", nodeId);
    const data = window.SQLFlowGraph._graphData;
    if (!data) {
        console.error("No graph data in SQLFlowGraph._graphData");
        return;
    }

    // Store the selected node ID globally if needed
    window.SQLFlowGraph.selectedNodeId = nodeId;

    const node = data.nodes.find((n) => n.id === nodeId);
    if (!node) {
        console.warn("Node not found for ID:", nodeId);
        return;
    }

    // Clear existing highlights
    window.SQLFlowGraph.clearHighlights();

    // Find all descendants
    const descendants = new Set();
    findDescendantsImproved(nodeId, descendants, data);
    // Add the current node to the set
    descendants.add(nodeId);

    // Highlight relevant nodes
    d3.selectAll(".node-group").each(function (d) {
        const inPath = descendants.has(d.id);
        d3.select(this).classed("dimmed", !inPath);
        d3.select(this).select(".node-rect")
            .classed("dimmed", !inPath)
            .classed("highlighted", d.id === nodeId);
        d3.select(this).selectAll(".node-text")
            .style("opacity", inPath ? 1 : 0.2);
    });

    // Highlight edges where both source and target are in the descendant set
    d3.selectAll(".link").each(function (d) {
        const sourceId = typeof d.source === 'object' ? d.source.id : d.source;
        const targetId = typeof d.target === 'object' ? d.target.id : d.target;
        const inPath = descendants.has(sourceId) && descendants.has(targetId);
        d3.select(this)
            .classed("dimmed", !inPath)
            .classed("highlighted", inPath);
    });

    // Keep nodes above edges
    const edgesG = d3.select(".edges");
    const nodesG = d3.select(".nodes");
    if (!edgesG.empty()) edgesG.lower();
    if (!nodesG.empty()) nodesG.raise();

    // Optionally center/zoom on the selected node
    centerViewOnNode(nodeId);
};

/**
 * Recursively find all descendants (child nodes) of a node.
 */
function findDescendantsImproved(sourceId, visited, data) {
    if (!data || !data.edges) {
        console.error("No valid graph data available");
        return;
    }
    const childEdges = data.edges.filter(e => {
        const source = typeof e.source === 'object' ? e.source.id : e.source;
        return source === sourceId;
    });

    for (const edge of childEdges) {
        const targetId = typeof edge.target === 'object' ? edge.target.id : edge.target;
        if (!visited.has(targetId)) {
            visited.add(targetId);
            findDescendantsImproved(targetId, visited, data);
        }
    }
}

/**
 * Center the view on a specific node by ID, with slight zoom.
 */
function centerViewOnNode(nodeId) {
    const nodeEl = d3.selectAll(".node-group").filter((d) => d.id === nodeId).node();
    if (nodeEl) {
        const svg = d3.select("#orgChart");
        const containerElement = svg.node().parentElement;
        if (!containerElement) return;

        const width = containerElement.clientWidth || 800;
        const height = containerElement.clientHeight || 600;

        const transform = nodeEl.getAttribute("transform");
        const match = /translate\(([-\d.]+),\s*([-\d.]+)\)/.exec(transform);
        if (match) {
            const x = parseFloat(match[1]);
            const y = parseFloat(match[2]);
            const scale = 1.2;
            svg.transition()
                .duration(750)
                .call(
                    d3.zoom().transform,
                    d3.zoomIdentity
                        .translate(width / 2 - x * scale, height / 2 - y * scale)
                        .scale(scale)
                );
        }
    }
}
