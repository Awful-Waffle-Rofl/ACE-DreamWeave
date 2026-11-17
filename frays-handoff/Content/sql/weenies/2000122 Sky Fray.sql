-- @owner: frays
-- @owns: wcid:2000122
-- @requires-restart: no
--
-- Frays: on the shore below the Temple of Xik Minru's floating platform (0xD5DA 157, 129.5), outside the Fray town's walls; takes players up to the platform floor (0xD5DA0035 163, 108, 116) where the group Frays will be.

DELETE FROM `weenie` WHERE `class_Id` = 2000122;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000122, 'dw-sky-fray-portal', 7, '2026-11-01 14:28:55') /* Portal */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000122,   1,      65536) /* ItemType - Portal */
     , (2000122,  16,         32) /* ItemUseable - Remote */
     , (2000122,  86,        300) /* MinLevel */
     , (2000122,  93,       3084) /* PhysicsState - Ethereal, ReportCollisions, Gravity, LightingOn */
     , (2000122, 111,          1) /* PortalBitmask - Unrestricted */
     , (2000122, 133,          4) /* ShowableOnRadar - ShowAlways */
     , (2000122, 9022,          1) /* PortalRealm */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000122,   1, True ) /* Stuck */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000122,  54,    -0.1) /* UseRadius */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000122,   1, 'Sky Fray') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000122,   1, 0x020005D2) /* Setup */
     , (2000122,   2, 0x09000003) /* MotionTable */
     , (2000122,   8, 0x0600106B) /* Icon */;

INSERT INTO `weenie_properties_position` (`object_Id`, `position_Type`, `obj_Cell_Id`, `origin_X`, `origin_Y`, `origin_Z`, `angles_W`, `angles_X`, `angles_Y`, `angles_Z`)
VALUES (2000122, 2, 0xD5DA0025, 109.688, 109.95, 116.005, -0.680206, 0, 0, -0.733021) /* Destination */
/* @teleloc 0xD5DA0025 [109.688004 109.949997 116.004997] -0.680206 0.000000 0.000000 -0.733021 */;
