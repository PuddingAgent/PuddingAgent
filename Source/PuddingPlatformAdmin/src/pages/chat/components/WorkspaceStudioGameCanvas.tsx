import Phaser from 'phaser';
import React from 'react';
import studioTilesetUrl from '../../../../public/assets/workspace-studio/studio-tiles-v2.png';
import type {
  WorkspaceStudioAgent,
  WorkspaceStudioAgentCommand,
  WorkspaceStudioAgentPosition,
  WorkspaceStudioObjectDefinition,
  WorkspaceStudioObjectId,
  WorkspaceStudioSceneEvent,
  WorkspaceStudioSceneStatus,
} from './workspaceStudio';
import {
  getWorkspaceStudioActiveEvent,
  getWorkspaceStudioAgentPosition,
  getWorkspaceStudioInteractionPosition,
  getWorkspaceStudioObjectStatusLabel,
  isWorkspaceStudioObjectActive,
  workspaceStudioAreas,
  workspaceStudioObjects,
} from './workspaceStudio';

interface WorkspaceStudioGameCanvasProps {
  agents: WorkspaceStudioAgent[];
  sceneStatus?: WorkspaceStudioSceneStatus;
  sceneEvents?: WorkspaceStudioSceneEvent[];
  selectedObjectId?: WorkspaceStudioObjectId;
  onAgentCommand?: (
    agentId: string,
    command: WorkspaceStudioAgentCommand,
  ) => void;
  onObjectSelect?: (objectId: WorkspaceStudioObjectId) => void;
}

interface WorkspaceStudioGameSnapshot {
  agents: WorkspaceStudioAgent[];
  sceneStatus?: WorkspaceStudioSceneStatus;
  sceneEvents?: WorkspaceStudioSceneEvent[];
  selectedObjectId?: WorkspaceStudioObjectId;
}

interface WorkspaceStudioGameCallbacks {
  onAgentCommand?: (
    agentId: string,
    command: WorkspaceStudioAgentCommand,
  ) => void;
  onObjectSelect?: (objectId: WorkspaceStudioObjectId) => void;
}

const gameWidth = 1600;
const gameHeight = 1000;
const materialTile = 32;
const spriteFrameWidth = 192;
const spriteFrameHeight = 208;
const agentSpriteDisplayWidth = 112;
const agentSpriteDisplayHeight = 124;
const studioTilesetKey = 'workspace-studio-tiles-v2';

const studioTileFrames = {
  floor: [0, 1, 2, 3, 4, 5, 6, 7],
  greenRug: {
    center: 8,
    top: 9,
    bottom: 10,
    left: 11,
    right: 12,
    topLeft: 13,
    topRight: 14,
    bottomLeft: 15,
    bottomRight: 15,
  },
  brownRug: {
    center: 16,
    top: 17,
    bottom: 18,
    left: 19,
    right: 20,
    topLeft: 21,
    topRight: 22,
    bottomLeft: 23,
    bottomRight: 23,
  },
  wall: [24, 25],
  baseboard: [26, 27],
};

const palette = {
  ink: 0x2a1a12,
  frameDark: 0x53311f,
  frameMid: 0xb66f3f,
  frameLight: 0xe5a46a,
  wall: 0xf7dfb3,
  wallShade: 0xe9c58c,
  floorA: 0xc98f53,
  floorB: 0xd6a365,
  floorLine: 0xb97d48,
  woodDark: 0x6b432e,
  woodMid: 0x8e6143,
  woodLight: 0xc18a58,
  cream: 0xfff2cf,
  greenDark: 0x4f7250,
  greenMid: 0x77945e,
  greenLight: 0x98b67a,
  blue: 0x8ab7bf,
  red: 0xc95b48,
  gold: 0xe2bb63,
  purple: 0x7c3aed,
};

function toWorldPosition(position: WorkspaceStudioAgentPosition): {
  x: number;
  y: number;
} {
  return {
    x: (position.x / 100) * gameWidth,
    y: (position.y / 100) * gameHeight,
  };
}

function textureKeyForAgent(agent: WorkspaceStudioAgent): string {
  return agent.spriteTextureKey;
}

function drawPixelLabel(
  scene: any,
  x: number,
  y: number,
  text: string,
  options: { persistent?: boolean } = {},
) {
  const label = scene.add.container(x, y);
  const textObject = scene.add
    .text(0, 0, text, {
      color: '#3a251a',
      fontFamily: 'Arial, sans-serif',
      fontSize: options.persistent ? '13px' : '12px',
      fontStyle: 'bold',
    })
    .setOrigin(0.5);
  const width = Math.max(46, textObject.width + 16);
  const background = scene.add
    .rectangle(0, 0, width, 22, 0xffefc6, options.persistent ? 0.9 : 0.78)
    .setStrokeStyle(2, 0x5e3a28, options.persistent ? 0.82 : 0.46);
  label.add([background, textObject]);
  label.setDepth(42);
  return label;
}

function createWorkspaceStudioScene(
  Phaser: any,
  snapshotRef: React.MutableRefObject<WorkspaceStudioGameSnapshot>,
  callbacksRef: React.MutableRefObject<WorkspaceStudioGameCallbacks>,
) {
  return class WorkspaceStudioScene extends Phaser.Scene {
    private snapshot: WorkspaceStudioGameSnapshot = snapshotRef.current;

    private root?: any;

    private objectNodes = new Map<WorkspaceStudioObjectId, any>();

    private agentNodes = new Map<string, any>();

    private hoverLabel?: any;

    private signalTween?: any;

    constructor() {
      super('WorkspaceStudioScene');
    }

    preload() {
      this.load.spritesheet(studioTilesetKey, studioTilesetUrl, {
        frameWidth: materialTile,
        frameHeight: materialTile,
      });
      const snapshot = snapshotRef.current;
      const loadedTextureKeys = new Set<string>();
      snapshot.agents.forEach((agent) => {
        const textureKey = textureKeyForAgent(agent);
        if (loadedTextureKeys.has(textureKey)) return;
        loadedTextureKeys.add(textureKey);
        this.load.spritesheet(textureKey, agent.spriteSheetUrl, {
          frameWidth: spriteFrameWidth,
          frameHeight: spriteFrameHeight,
        });
      });
    }

    create() {
      this.cameras.main.setBackgroundColor('#efe8dc');
      this.root = this.add.container(0, 0);
      this.ensureMaterialTextures();
      this.renderSnapshot(snapshotRef.current);
    }

    setSnapshot(next: WorkspaceStudioGameSnapshot) {
      this.snapshot = next;
      if (!this.root) return;
      this.renderSnapshot(next);
    }

    private ensureMaterialTextures() {
      if (!this.textures.exists('workspace-studio-floor-plank')) {
        const g = this.make.graphics({ x: 0, y: 0, add: false });
        g.fillStyle(0xc98f53, 1).fillRect(
          0,
          0,
          materialTile * 2,
          materialTile * 2,
        );
        g.fillStyle(0xd8a565, 1).fillRect(0, 0, materialTile * 2, 5);
        g.fillStyle(0xb97844, 1).fillRect(
          0,
          materialTile - 1,
          materialTile * 2,
          2,
        );
        g.fillStyle(0xb97844, 1).fillRect(
          0,
          materialTile * 2 - 2,
          materialTile * 2,
          2,
        );
        g.fillStyle(0xe0af6c, 0.72).fillRect(7, 11, 43, 2);
        g.fillStyle(0xe0af6c, 0.5).fillRect(37, 43, 22, 2);
        g.fillStyle(0xaa6c3f, 0.48).fillRect(10, 22, 18, 1);
        g.fillStyle(0xaa6c3f, 0.42).fillRect(44, 58, 13, 1);
        g.fillStyle(0xb97844, 0.62).fillRect(
          materialTile - 1,
          0,
          2,
          materialTile,
        );
        g.fillStyle(0xb97844, 0.45).fillRect(
          materialTile * 2 - 1,
          materialTile,
          1,
          materialTile,
        );
        g.generateTexture(
          'workspace-studio-floor-plank',
          materialTile * 2,
          materialTile * 2,
        );
        g.destroy();
      }
      if (!this.textures.exists('workspace-studio-wallpaper')) {
        const g = this.make.graphics({ x: 0, y: 0, add: false });
        g.fillStyle(0xf7dfb3, 1).fillRect(
          0,
          0,
          materialTile * 2,
          materialTile * 2,
        );
        g.fillStyle(0xfbe8c3, 1).fillRect(0, 0, materialTile * 2, 4);
        g.fillStyle(0xe9c58c, 0.46).fillRect(
          0,
          materialTile - 1,
          materialTile * 2,
          2,
        );
        g.fillStyle(0xe9c58c, 0.32).fillRect(
          materialTile - 1,
          0,
          2,
          materialTile * 2,
        );
        g.fillStyle(0xdcae70, 0.58).fillCircle(12, 14, 2);
        g.fillStyle(0xdcae70, 0.44).fillCircle(46, 42, 2);
        g.fillStyle(0xc88355, 0.36).fillRect(10, 46, 14, 2);
        g.fillStyle(0xc88355, 0.28).fillRect(42, 18, 10, 2);
        g.generateTexture(
          'workspace-studio-wallpaper',
          materialTile * 2,
          materialTile * 2,
        );
        g.destroy();
      }
      if (!this.textures.exists('workspace-studio-baseboard')) {
        const g = this.make.graphics({ x: 0, y: 0, add: false });
        g.fillStyle(0xcd8a56, 1).fillRect(0, 0, materialTile * 2, 18);
        g.fillStyle(0xe2a46a, 1).fillRect(0, 0, materialTile * 2, 4);
        g.fillStyle(0xad7046, 1).fillRect(0, 14, materialTile * 2, 4);
        g.fillStyle(0xb87949, 0.7).fillRect(8, 7, 22, 2);
        g.fillStyle(0xb87949, 0.52).fillRect(40, 9, 16, 2);
        g.generateTexture('workspace-studio-baseboard', materialTile * 2, 18);
        g.destroy();
      }
    }

    private addAtlasTile(
      x: number,
      y: number,
      frame: number,
      cropWidth = materialTile,
      cropHeight = materialTile,
      alpha = 1,
    ) {
      const tileImage = this.add
        .image(x, y, studioTilesetKey, frame)
        .setOrigin(0)
        .setAlpha(alpha);
      if (cropWidth < materialTile || cropHeight < materialTile) {
        tileImage.setCrop(0, 0, cropWidth, cropHeight);
      }
      this.root?.add(tileImage);
      return tileImage;
    }

    private addTiledMaterial(
      frames: number[],
      x: number,
      y: number,
      width: number,
      height: number,
      alpha = 1,
    ) {
      const columns = Math.ceil(width / materialTile);
      const rows = Math.ceil(height / materialTile);
      for (let row = 0; row < rows; row += 1) {
        for (let col = 0; col < columns; col += 1) {
          const frame = frames[(col * 3 + row * 5) % frames.length];
          const cropWidth = Math.min(materialTile, width - col * materialTile);
          const cropHeight = Math.min(
            materialTile,
            height - row * materialTile,
          );
          this.addAtlasTile(
            x + col * materialTile,
            y + row * materialTile,
            frame,
            cropWidth,
            cropHeight,
            alpha,
          );
        }
      }
    }

    private addRugPatch(
      x: number,
      y: number,
      columns: number,
      rows: number,
      frames: typeof studioTileFrames.greenRug,
      alpha = 1,
    ) {
      for (let row = 0; row < rows; row += 1) {
        for (let col = 0; col < columns; col += 1) {
          let frame = frames.center;
          if (row === 0 && col === 0) frame = frames.topLeft;
          else if (row === 0 && col === columns - 1) frame = frames.topRight;
          else if (row === rows - 1 && col === 0) frame = frames.bottomLeft;
          else if (row === rows - 1 && col === columns - 1)
            frame = frames.bottomRight;
          else if (row === 0) frame = frames.top;
          else if (row === rows - 1) frame = frames.bottom;
          else if (col === 0) frame = frames.left;
          else if (col === columns - 1) frame = frames.right;
          this.addAtlasTile(
            x + col * materialTile,
            y + row * materialTile,
            frame,
            materialTile,
            materialTile,
            alpha,
          );
        }
      }
    }

    private renderSnapshot(snapshot: WorkspaceStudioGameSnapshot) {
      this.snapshot = snapshot;
      this.root?.removeAll(true);
      this.objectNodes.clear();
      this.agentNodes.clear();
      this.signalTween?.stop();
      this.signalTween = undefined;

      this.drawRoom(snapshot);
      this.drawAgents(snapshot);
      this.drawSceneEvent(snapshot);
    }

    private drawRoom(snapshot: WorkspaceStudioGameSnapshot) {
      const hasTask = (snapshot.sceneStatus?.activeTaskCount ?? 0) > 0;
      const hasRecent = (snapshot.sceneStatus?.recentActivityCount ?? 0) > 0;
      const g = this.add.graphics();
      this.root?.add(g);

      g.fillStyle(palette.ink, 1).fillRect(0, 0, gameWidth, gameHeight);
      g.fillStyle(0x3d2518, 1).fillRect(
        18,
        18,
        gameWidth - 36,
        gameHeight - 36,
      );
      g.fillStyle(palette.frameMid, 1).fillRect(
        28,
        28,
        gameWidth - 56,
        gameHeight - 56,
      );
      g.fillStyle(palette.frameLight, 1).fillRect(36, 36, gameWidth - 72, 12);
      g.fillStyle(0xc87945, 1).fillRect(
        36,
        gameHeight - 48,
        gameWidth - 72,
        12,
      );
      g.fillStyle(0xf5d9a7, 1).fillRect(72, 72, gameWidth - 144, 174);
      g.fillStyle(0xc98f53, 1).fillRect(
        72,
        280,
        gameWidth - 144,
        gameHeight - 340,
      );
      g.fillStyle(0xb97c4a, 1).fillRect(
        72,
        gameHeight - 74,
        gameWidth - 144,
        46,
      );
      this.addTiledMaterial(
        studioTileFrames.wall,
        72,
        72,
        gameWidth - 144,
        174,
      );
      this.addTiledMaterial(
        studioTileFrames.baseboard,
        72,
        246,
        gameWidth - 144,
        34,
      );
      this.addTiledMaterial(
        studioTileFrames.floor,
        72,
        280,
        gameWidth - 144,
        gameHeight - 350,
      );

      const detail = this.add.graphics();
      this.root?.add(detail);
      detail.fillStyle(0xf7dca9, 0.64).fillRect(72, 72, gameWidth - 144, 8);
      detail.fillStyle(0xe0a367, 0.24).fillRect(96, 304, gameWidth - 192, 26);
      detail
        .fillStyle(0xb47745, 0.24)
        .fillRect(96, gameHeight - 118, gameWidth - 192, 22);
      detail.fillStyle(0xd78c53, 0.5).fillRect(72, 280, gameWidth - 144, 10);
      detail.fillStyle(0xf1c47f, 0.5).fillRect(72, 246, gameWidth - 144, 5);

      this.drawWallDecor(detail, snapshot, hasTask, hasRecent);
      this.drawZones(detail);
      this.drawFurniture(detail, hasTask, hasRecent);
      this.drawInteractiveObjects(snapshot, hasTask, hasRecent);
    }

    private drawWallDecor(
      g: any,
      snapshot: WorkspaceStudioGameSnapshot,
      hasTask: boolean,
      hasRecent: boolean,
    ) {
      g.fillStyle(0xb77855, 1)
        .fillRect(140, 116, 10, 72)
        .fillRect(270, 116, 10, 72);
      g.fillStyle(0xf5e7c5, 1)
        .fillRect(150, 120, 120, 60)
        .lineStyle(4, 0x9d714f, 1)
        .strokeRect(150, 120, 120, 60);
      g.lineStyle(3, 0xb98b61, 1)
        .lineBetween(210, 120, 210, 180)
        .lineBetween(150, 150, 270, 150);
      g.fillStyle(0xc18a64, 1).fillRect(148, 180, 124, 8);
      g.fillStyle(0x7e654e, 1)
        .fillRect(296, 132, 36, 34)
        .lineStyle(4, 0x8c6045, 1)
        .strokeRect(296, 132, 36, 34);
      g.fillStyle(0x75a177, 1).fillRect(306, 142, 12, 12);
      g.fillStyle(0x8f6a4a, 1)
        .fillRect(342, 126, 34, 44)
        .lineStyle(4, 0x6a4d39, 1)
        .strokeRect(342, 126, 34, 44);
      g.fillStyle(0xc45c42, 1).fillRect(354, 138, 10, 12);
      g.fillStyle(0x6f8d66, 1).fillRect(388, 142, 28, 40);
      g.fillStyle(0x4f6f56, 1).fillRect(398, 132, 18, 18);
      g.fillStyle(0xf6e6c6, 1)
        .fillRect(410, 154, 24, 40)
        .lineStyle(4, 0x7a563d, 1)
        .strokeRect(410, 154, 24, 40);
      g.fillStyle(0x81634a, 1).fillRect(570, 126, 178, 60);
      [0xc65b45, 0xdfc16b, 0x6fa27b, 0x88a0a8, 0xe9d5a2].forEach(
        (color, index) => {
          for (let x = 0; x < 5; x += 1) {
            g.fillStyle(color, 1).fillRect(
              586 + x * 30,
              142 + index * 6,
              12,
              10,
            );
          }
        },
      );
      g.fillStyle(0x6a4d39, 1).fillRect(562, 186, 194, 10);
      g.fillStyle(0x7a5138, 1).fillRect(770, 148, 128, 16);
      g.fillStyle(0xcda54f, 1).fillRect(842, 136, 18, 18);
      g.fillStyle(0x78a36d, 1).fillRect(804, 132, 18, 22);
      g.fillStyle(0x9c7656, 1).fillRect(930, 124, 132, 52);
      g.fillStyle(0xeadfc6, 1).fillRect(948, 140, 96, 18);
      g.fillStyle(hasTask ? 0xa172dc : 0x8aa17a, 1).fillRect(948, 166, 84, 8);
      this.drawWallInfoBoard({
        x: 1216,
        y: 112,
        width: 242,
        height: 76,
        title: '工作室状态',
        value: `${snapshot.agents.length} 个 Agent`,
        active: snapshot.agents.length > 0,
      });
      this.drawWallInfoBoard({
        x: 1216,
        y: 198,
        width: 242,
        height: 64,
        title: '最近活动',
        value: hasRecent
          ? `${snapshot.sceneStatus?.recentActivityCount ?? 0} 条活动`
          : '暂无活动',
        active: hasRecent,
      });
      g.fillStyle(0x9e6d44, 1).fillRect(640, 282, 132, 16);
      g.fillStyle(0xf2cb75, 1)
        .fillRect(86, 330, 18, 42)
        .fillRect(1480, 330, 18, 42);
      g.fillStyle(0x7a563d, 1)
        .fillRect(96, 320, 6, 16)
        .fillRect(1490, 320, 6, 16);
    }

    private drawWallInfoBoard(input: {
      x: number;
      y: number;
      width: number;
      height: number;
      title: string;
      value: string;
      active: boolean;
    }) {
      const { x, y, width, height, title, value, active } = input;
      const board = this.add.graphics();
      this.root?.add(board);
      board
        .fillStyle(0x6a4d39, 1)
        .fillRect(x - 5, y - 5, width + 10, height + 10);
      board.fillStyle(0xb98a5e, 1).fillRect(x, y, width, height);
      board
        .fillStyle(active ? 0xfff1c8 : 0xe9dec4, 1)
        .fillRect(x + 10, y + 10, width - 20, height - 20);
      board
        .fillStyle(active ? palette.purple : 0x8aa17a, 0.82)
        .fillRect(x + 18, y + height - 16, width - 52, 8);
      board.fillStyle(0x4f3425, 0.82).fillRect(x + width - 28, y + 18, 10, 10);
      if (active) {
        board
          .fillStyle(palette.gold, 0.9)
          .fillRect(x + width - 40, y + 18, 10, 10);
      }

      const titleText = this.add.text(x + 18, y + 15, title, {
        color: '#3a251a',
        fontFamily: 'Arial, sans-serif',
        fontSize: '15px',
        fontStyle: 'bold',
      });
      const valueText = this.add.text(x + 18, y + 39, value, {
        color: '#6f513d',
        fontFamily: 'Arial, sans-serif',
        fontSize: '13px',
        fontStyle: 'bold',
      });
      this.root?.add(titleText);
      this.root?.add(valueText);
    }

    private drawZones(g: any) {
      g.fillStyle(0x5f7d4c, 0.28).fillEllipse(390, 660, 520, 220);
      this.addRugPatch(188, 600, 12, 4, studioTileFrames.greenRug, 0.94);

      g.fillStyle(0x8a5f49, 0.3).fillEllipse(1282, 660, 420, 210);
      this.addRugPatch(1136, 604, 9, 4, studioTileFrames.brownRug, 0.92);

      g.fillStyle(0x7a8f71, 0.28).fillEllipse(780, 626, 310, 98);
      g.fillStyle(0xe0c487, 0.42).fillEllipse(780, 624, 236, 76);
      g.lineStyle(4, 0x7a563d, 0.58)
        .lineBetween(648, 588, 910, 588)
        .lineBetween(642, 630, 918, 630);
      g.fillStyle(0x6f8f75, 1).fillRect(720, 604, 150, 58);
      g.fillStyle(0x8aa889, 1).fillRect(728, 610, 134, 42);
      g.fillStyle(0xd4a45c, 1).fillRect(752, 622, 62, 22);
      g.fillStyle(0xf7e7bd, 1).fillRect(836, 640, 42, 30);
      g.fillStyle(palette.red, 1).fillRect(896, 630, 34, 20);
      g.fillStyle(palette.greenLight, 1).fillRect(952, 630, 36, 20);
      g.fillStyle(0x846044, 0.95)
        .fillRect(686, 612, 28, 32)
        .fillRect(876, 612, 28, 32);

      const border = this.add.graphics();
      this.root?.add(border);
      border.lineStyle(3, 0x4f6b3d, 0.72).strokeRect(188, 600, 384, 128);
      border.lineStyle(3, 0x815c43, 0.72).strokeRect(1136, 604, 288, 128);
    }

    private drawFurniture(g: any, hasTask: boolean, hasRecent: boolean) {
      g.fillStyle(0x6b432e, 0.2).fillEllipse(186, 530, 250, 64);
      g.fillStyle(palette.greenMid, 1).fillRect(96, 486, 220, 28);
      g.fillStyle(palette.greenLight, 1).fillRect(118, 456, 124, 48);
      g.fillStyle(palette.greenDark, 1).fillRect(106, 506, 184, 18);
      g.fillStyle(palette.woodMid, 1).fillRect(152, 392, 44, 106);
      g.fillStyle(0xb08a65, 1).fillRect(160, 402, 28, 84);
      g.fillStyle(0xf7d889, 1).fillRect(82, 474, 36, 30);
      g.fillStyle(0xfdf0c1, 1).fillRect(92, 460, 18, 16);
      g.fillStyle(palette.woodDark, 1).fillRect(96, 502, 7, 86);
      g.fillStyle(0x6f4a35, 1).fillRect(248, 514, 64, 64);
      g.fillStyle(0x8b5c3d, 1).fillRect(254, 506, 52, 12);
      [0xf3d98f, 0x98b67a, 0xd75a49, 0xf3d98f, 0xb7c9cb].forEach(
        (color, index) => {
          g.fillStyle(color, 1).fillRect(262 + index * 10, 526, 7, 14);
        },
      );
      g.fillStyle(0xf2ca65, 1).fillRect(286, 548, 18, 8);

      g.fillStyle(0x5a3727, 0.24).fillEllipse(536, 510, 230, 52);
      g.fillStyle(palette.woodDark, 1).fillRect(442, 460, 144, 42);
      g.fillStyle(palette.woodLight, 1).fillRect(452, 452, 122, 28);
      g.fillStyle(0xf7e6bf, 1).fillRect(468, 462, 76, 14);
      g.fillStyle(0xddebdc, 1).fillRect(488, 416, 54, 42);
      g.lineStyle(4, 0x65777a, 1).strokeRect(488, 416, 54, 42);
      g.fillStyle(hasTask ? 0x8bd3dd : 0xaed7bb, 1)
        .fillRect(500, 430, 24, 5)
        .fillRect(530, 430, 8, 5);
      g.fillStyle(0x6b6b58, 1).fillRect(560, 454, 58, 50);
      g.fillStyle(hasTask ? 0x54c0ad : 0x9e8b73, 1)
        .fillRect(574, 470, 10, 7)
        .fillRect(600, 470, 8, 7);
      g.fillStyle(0x2f1e16, 1).fillRect(612, 504, 46, 5);
      g.fillStyle(0xfff1c8, 1).fillRect(462, 438, 22, 18);
      g.fillStyle(palette.red, 1).fillRect(584, 436, 16, 10);
      g.fillStyle(0xf2cb75, 0.95).fillRect(548, 432, 16, 58);
      g.fillStyle(0xf9e5a4, 0.34).fillEllipse(548, 486, 118, 52);

      g.fillStyle(0x7d553c, 1).fillRect(1018, 336, 150, 80);
      g.fillStyle(0xb7895f, 1).fillRect(1030, 326, 126, 76);
      g.fillStyle(0xf6e6c6, 1).fillRect(1042, 318, 34, 22);
      g.fillStyle(0x77a36d, 1).fillRect(1078, 322, 38, 20);
      g.fillStyle(palette.red, 1).fillRect(1118, 314, 34, 28);
      g.fillStyle(0x76543e, 1)
        .fillRect(1040, 386, 20, 10)
        .fillRect(1080, 386, 20, 10)
        .fillRect(1122, 386, 20, 10);
      g.fillStyle(0x6b4b39, 0.22).fillEllipse(1238, 538, 90, 38);
      g.fillStyle(0x806650, 1).fillRect(1210, 462, 64, 96);
      g.fillStyle(0x4f8d6a, 1)
        .fillRect(1222, 474, 8, 8)
        .fillRect(1254, 492, 8, 8)
        .fillRect(1238, 516, 8, 8);

      g.fillStyle(0x6d4c38, 0.28).fillEllipse(1360, 540, 196, 58);
      g.fillStyle(0x8f6b55, 1).fillRect(1268, 472, 48, 78);
      g.fillStyle(0xf0cf90, 1).fillRect(1278, 480, 24, 38);
      g.fillStyle(0xd5a66a, 1).fillRect(1290, 528, 18, 10);
      g.fillStyle(0x9e7c64, 1).fillRect(1300, 486, 80, 44);
      g.fillStyle(0x614b3d, 1).fillRect(1366, 438, 72, 86);
      g.fillStyle(0x8b6f5a, 1).fillRect(1360, 520, 90, 16);
      g.fillStyle(0xc39773, 1).fillRect(1380, 504, 58, 20);
      g.fillStyle(0xd8b38d, 1).fillRect(1312, 510, 58, 18);
      g.fillStyle(0xe2c77e, 1).fillRect(1288, 540, 20, 14);
      g.fillStyle(0x8c6045, 1).fillRect(1328, 802, 58, 48);
      g.fillStyle(0x714732, 1)
        .fillRect(1340, 816, 34, 5)
        .fillRect(1340, 832, 34, 5);

      g.fillStyle(palette.greenMid, 1).fillRect(728, 888, 140, 28);
      g.fillStyle(0x6c4d37, 1).fillRect(620, 886, 46, 64);
      g.fillStyle(0xe9d5a2, 1)
        .fillRect(632, 904, 22, 5)
        .fillRect(632, 922, 22, 5);
      g.fillStyle(0x583420, 1).fillRect(696, 928, 92, 54);
      g.fillStyle(0x251812, 1).fillRect(786, 928, 90, 54);
      g.fillStyle(0xe0b44d, 1).fillCircle(740, 955, 5);
      g.fillStyle(0xf3e6d0, 1).fillRect(910, 874, 44, 58);
      g.fillStyle(hasRecent ? palette.red : 0xc08d5e, 1).fillRect(
        952,
        872,
        18,
        26,
      );
      g.fillStyle(0x8b5d3c, 1)
        .fillRect(920, 888, 28, 5)
        .fillRect(920, 902, 28, 5);
      g.fillStyle(0xffffff, 1).fillRect(890, 904, 36, 12);

      g.fillStyle(palette.greenMid, 1).fillRect(880, 490, 34, 36);
      g.fillStyle(0x4f8d6a, 1)
        .fillRect(888, 474, 18, 18)
        .fillRect(908, 492, 16, 12);
      g.fillStyle(0x8d694e, 1).fillRect(896, 524, 24, 12);
      g.fillStyle(palette.greenMid, 1).fillRect(118, 386, 22, 90);
      g.fillStyle(0x4f8d6a, 1)
        .fillRect(104, 376, 54, 34)
        .fillRect(84, 406, 38, 24);
      g.fillStyle(0x8d694e, 1).fillRect(118, 468, 38, 16);

      g.fillStyle(0xd1a869, 0.35).fillEllipse(350, 820, 160, 36);
      g.fillStyle(0x7a563d, 1).fillRect(344, 796, 54, 44);
      g.fillStyle(0xf4d88b, 1)
        .fillRect(356, 808, 30, 6)
        .fillRect(356, 826, 30, 6);
      g.fillStyle(0x6f9871, 1).fillRect(1138, 802, 42, 48);
      g.fillStyle(0x4f8d6a, 1)
        .fillRect(1126, 782, 28, 26)
        .fillRect(1160, 790, 26, 20);
    }

    private drawInteractiveObjects(
      snapshot: WorkspaceStudioGameSnapshot,
      hasTask: boolean,
      hasRecent: boolean,
    ) {
      workspaceStudioObjects.forEach((object) => {
        this.addObject(
          object,
          isWorkspaceStudioObjectActive(
            object.objectId,
            snapshot.sceneStatus,
            snapshot.sceneEvents,
          ),
          getWorkspaceStudioObjectStatusLabel(
            object.objectId,
            snapshot.sceneStatus,
          ),
        );
      });

      if (hasTask || getWorkspaceStudioActiveEvent(snapshot.sceneEvents)) {
        const meetingArea = workspaceStudioAreas.find(
          (area) => area.areaId === 'meeting',
        );
        if (meetingArea) {
          const pulse = this.add.circle(
            meetingArea.bounds.x + meetingArea.bounds.width / 2,
            meetingArea.bounds.y + meetingArea.bounds.height / 2,
            18,
            palette.purple,
            0.22,
          );
          this.root?.add(pulse);
          this.tweens.add({
            targets: pulse,
            alpha: { from: 0.08, to: 0.28 },
            scale: { from: 0.9, to: 1.25 },
            yoyo: true,
            repeat: -1,
            duration: 900,
            ease: 'Sine.easeInOut',
          });
        }
      }

      if (hasRecent) {
        const mailbox = workspaceStudioObjects.find(
          (object) => object.objectId === 'mailbox',
        );
        if (mailbox) {
          const flag = this.add.rectangle(
            mailbox.bounds.x + mailbox.bounds.width - 14,
            mailbox.bounds.y + 14,
            16,
            18,
            palette.red,
            0.92,
          );
          this.root?.add(flag);
          this.tweens.add({
            targets: flag,
            y: flag.y - 6,
            yoyo: true,
            repeat: -1,
            duration: 700,
            ease: 'Sine.easeInOut',
          });
        }
      }

      if (snapshot.selectedObjectId) {
        const node = this.objectNodes.get(snapshot.selectedObjectId);
        node?.setData('selected', true);
      }
    }

    private addObject(
      object: WorkspaceStudioObjectDefinition,
      active: boolean,
      statusLabel: string,
    ) {
      const { objectId, bounds } = object;
      const { x, y, width, height } = bounds;
      const zone = this.add
        .zone(x + width / 2, y + height / 2, width, height)
        .setOrigin(0.5)
        .setInteractive({ useHandCursor: object.interactive });
      zone.setData('objectId', objectId);
      this.objectNodes.set(objectId, zone);
      this.root?.add(zone);

      const selected = objectId === this.snapshot.selectedObjectId;
      const outlineColor = active ? 0x7c3aed : 0x7a563d;
      const hoverPanel = this.add
        .rectangle(
          x + width / 2,
          y + height / 2,
          width,
          height,
          active ? 0xefe1ff : 0xfff8e8,
          0,
        )
        .setStrokeStyle(4, outlineColor, selected ? 0.92 : 0);
      this.root?.add(hoverPanel);

      if (selected) {
        this.root?.add(
          drawPixelLabel(
            this,
            x + width / 2,
            y - 14,
            statusLabel || object.shortLabel,
            { persistent: true },
          ),
        );
        const pointer = this.add
          .triangle(
            x + width / 2,
            y + height + 16,
            0,
            0,
            16,
            0,
            8,
            12,
            0xfff1c8,
            0.95,
          )
          .setStrokeStyle(3, 0x4f3425, 0.95);
        this.root?.add(pointer);
        this.tweens.add({
          targets: pointer,
          y: pointer.y + 6,
          yoyo: true,
          repeat: -1,
          duration: 620,
          ease: 'Sine.easeInOut',
        });
      }

      if (active) {
        const statusLight = this.add.circle(
          x + width - 12,
          y + 14,
          7,
          0x7c3aed,
          0.92,
        );
        const statusHalo = this.add.circle(
          x + width - 12,
          y + 14,
          16,
          0x7c3aed,
          0.16,
        );
        this.root?.add(statusHalo);
        this.root?.add(statusLight);
        this.tweens.add({
          targets: [statusLight, statusHalo],
          alpha: { from: 0.28, to: 0.95 },
          scale: { from: 0.86, to: 1.14 },
          yoyo: true,
          repeat: -1,
          duration: 820,
          ease: 'Sine.easeInOut',
        });
      }

      zone.on('pointerover', () => {
        hoverPanel.setFillStyle(
          active ? 0xefe1ff : 0xfff8e8,
          active ? 0.2 : 0.16,
        );
        hoverPanel.setStrokeStyle(4, outlineColor, active ? 0.95 : 0.64);
        this.showHoverLabel(
          x + width / 2,
          y - 42,
          `${object.label} · ${object.description}`,
        );
      });
      zone.on('pointerout', () => {
        hoverPanel.setFillStyle(active ? 0xefe1ff : 0xfff8e8, 0);
        hoverPanel.setStrokeStyle(
          4,
          outlineColor,
          objectId === this.snapshot.selectedObjectId ? 0.92 : 0,
        );
        this.hideHoverLabel();
      });
      zone.on('pointerdown', () =>
        callbacksRef.current.onObjectSelect?.(objectId),
      );

      if (active) {
        this.tweens.add({
          targets: hoverPanel,
          alpha: { from: 0.35, to: 0.95 },
          yoyo: true,
          repeat: -1,
          duration: 900,
          ease: 'Sine.easeInOut',
        });
      }
    }

    private drawAgents(snapshot: WorkspaceStudioGameSnapshot) {
      const positionsByAgentId = snapshot.agents.reduce<
        Record<string, WorkspaceStudioAgentPosition>
      >((result, item, index) => {
        result[item.agentId] = getWorkspaceStudioAgentPosition(
          index,
          snapshot.agents.length,
          item.state,
        );
        return result;
      }, {});
      const activeEvent = getWorkspaceStudioActiveEvent(snapshot.sceneEvents);
      let activeEventPositions:
        | {
            source: WorkspaceStudioAgentPosition;
            target: WorkspaceStudioAgentPosition;
          }
        | undefined;
      if (activeEvent?.targetAgentId) {
        const source = positionsByAgentId[activeEvent.sourceAgentId];
        const target = positionsByAgentId[activeEvent.targetAgentId];
        if (source && target)
          activeEventPositions = {
            source: getWorkspaceStudioInteractionPosition(source, target),
            target,
          };
      }

      snapshot.agents.forEach((agent, index) => {
        const basePosition =
          positionsByAgentId[agent.agentId] ??
          getWorkspaceStudioAgentPosition(
            index,
            snapshot.agents.length,
            agent.state,
          );
        const position =
          activeEvent?.sourceAgentId === agent.agentId && activeEventPositions
            ? activeEventPositions.source
            : basePosition;
        const world = toWorldPosition(position);
        const container = this.add
          .container(world.x, world.y)
          .setDepth(Math.round(world.y));
        const shadow = this.add.ellipse(0, 38, 78, 18, 0x453424, 0.18);
        const textureKey = textureKeyForAgent(agent);
        const frame = agent.spriteRow * 8;
        const sprite = this.textures.exists(textureKey)
          ? this.add.sprite(0, -16, textureKey, frame)
          : this.add
              .rectangle(0, -22, 58, 94, 0xf7f4ed, 1)
              .setStrokeStyle(3, 0x4f463c, 1);
        if ('setDisplaySize' in sprite)
          sprite.setDisplaySize(
            agentSpriteDisplayWidth,
            agentSpriteDisplayHeight,
          );
        if ('anims' in sprite && agent.spriteFrameCount > 1) {
          const animKey = `${textureKey}:${agent.state}:${agent.spriteRow}:${agent.spriteFrameCount}`;
          if (!this.anims.exists(animKey)) {
            this.anims.create({
              key: animKey,
              frames: this.anims.generateFrameNumbers(textureKey, {
                start: agent.spriteRow * 8,
                end: agent.spriteRow * 8 + agent.spriteFrameCount - 1,
              }),
              frameRate: agent.state === 'working' ? 7 : 5,
              repeat: -1,
            });
          }
          sprite.play(animKey);
        }
        const name = this.add
          .text(0, 70, agent.name, {
            color: agent.selected ? '#5b2ee8' : '#4b3022',
            fontFamily: 'Arial, sans-serif',
            fontSize: '17px',
            fontStyle: 'bold',
          })
          .setOrigin(0.5)
          .setStroke('#fff1c8', 4);
        const zone = this.add
          .zone(0, -14, 128, 158)
          .setInteractive({ useHandCursor: true });
        container.add([shadow, sprite, name, zone]);
        this.root?.add(container);
        this.agentNodes.set(agent.agentId, container);

        zone.on('pointerover', () =>
          this.showHoverLabel(
            world.x,
            world.y - 128,
            `${agent.name} · ${agent.activity}`,
          ),
        );
        zone.on('pointerout', () => this.hideHoverLabel());
        zone.on('pointerdown', () =>
          callbacksRef.current.onAgentCommand?.(agent.agentId, 'chat'),
        );

        if (agent.state === 'working') {
          const dot = this.add.circle(58, -64, 6, 0x22d3ee, 1);
          container.add(dot);
          this.tweens.add({
            targets: dot,
            alpha: 0.24,
            yoyo: true,
            repeat: -1,
            duration: 720,
          });
        }
        if (agent.selected) {
          const ring = this.add
            .ellipse(0, 40, 98, 24)
            .setStrokeStyle(4, 0x7c3aed, 0.9);
          container.addAt(ring, 1);
        }
      });
    }

    private drawSceneEvent(snapshot: WorkspaceStudioGameSnapshot) {
      const activeEvent = getWorkspaceStudioActiveEvent(snapshot.sceneEvents);
      if (!activeEvent?.targetAgentId) return;
      const source = this.agentNodes.get(activeEvent.sourceAgentId);
      const target = this.agentNodes.get(activeEvent.targetAgentId);
      if (!source || !target) return;
      const g = this.add.graphics().setDepth(850);
      g.lineStyle(5, 0x22d3ee, 0.58).lineBetween(
        source.x,
        source.y - 68,
        target.x,
        target.y - 68,
      );
      this.root?.add(g);
      const bubble = drawPixelLabel(
        this,
        source.x,
        source.y - 154,
        activeEvent.text,
        { persistent: true },
      );
      this.root?.add(bubble);
      this.signalTween = this.tweens.add({
        targets: g,
        alpha: 0.28,
        yoyo: true,
        repeat: -1,
        duration: 640,
      });
    }

    private showHoverLabel(x: number, y: number, text: string) {
      this.hideHoverLabel();
      this.hoverLabel = drawPixelLabel(this, x, y, text, { persistent: true });
      this.root?.add(this.hoverLabel);
    }

    private hideHoverLabel() {
      this.hoverLabel?.destroy();
      this.hoverLabel = undefined;
    }
  };
}

const WorkspaceStudioGameCanvas: React.FC<WorkspaceStudioGameCanvasProps> = ({
  agents,
  sceneStatus,
  sceneEvents,
  selectedObjectId,
  onAgentCommand,
  onObjectSelect,
}) => {
  const hostRef = React.useRef<HTMLDivElement>(null);
  const gameRef = React.useRef<any>(undefined);
  const sceneRef = React.useRef<any>(undefined);
  const snapshotRef = React.useRef<WorkspaceStudioGameSnapshot>({
    agents,
    sceneStatus,
    sceneEvents,
    selectedObjectId,
  });
  const callbacksRef = React.useRef<WorkspaceStudioGameCallbacks>({
    onAgentCommand,
    onObjectSelect,
  });

  const spriteSignature = React.useMemo(
    () =>
      agents
        .map((agent) => `${agent.agentId}:${agent.spriteSheetUrl}`)
        .join('|'),
    [agents],
  );

  React.useEffect(() => {
    snapshotRef.current = {
      agents,
      sceneStatus,
      sceneEvents,
      selectedObjectId,
    };
    callbacksRef.current = { onAgentCommand, onObjectSelect };
    sceneRef.current?.setSnapshot(snapshotRef.current);
  }, [
    agents,
    onAgentCommand,
    onObjectSelect,
    sceneEvents,
    sceneStatus,
    selectedObjectId,
  ]);

  React.useEffect(() => {
    let disposed = false;
    if (!hostRef.current) return undefined;
    const SceneClass = createWorkspaceStudioScene(
      Phaser,
      snapshotRef,
      callbacksRef,
    );
    const game = new Phaser.Game({
      type: Phaser.AUTO,
      parent: hostRef.current,
      width: gameWidth,
      height: gameHeight,
      backgroundColor: '#efe8dc',
      pixelArt: true,
      antialias: false,
      scale: {
        mode: Phaser.Scale.FIT,
        autoCenter: Phaser.Scale.CENTER_BOTH,
        width: gameWidth,
        height: gameHeight,
      },
      scene: SceneClass,
    });
    gameRef.current = game;
    game.events.once('ready', () => {
      if (disposed) return;
      sceneRef.current = game.scene.getScene('WorkspaceStudioScene');
      sceneRef.current?.setSnapshot(snapshotRef.current);
    });

    return () => {
      disposed = true;
      sceneRef.current = undefined;
      gameRef.current?.destroy(true);
      gameRef.current = undefined;
    };
  }, [spriteSignature]);

  return (
    <div
      ref={hostRef}
      style={{ width: '100%', height: '100%' }}
      aria-label="工作室 2D 场景"
    />
  );
};

export default React.memo(WorkspaceStudioGameCanvas);
