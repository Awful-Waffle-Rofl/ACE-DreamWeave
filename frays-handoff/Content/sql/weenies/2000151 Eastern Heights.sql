-- @owner: frays
-- @owns: wcid:2000151
-- @requires-restart: no
--
-- Frays: the right of three portals inside the Temple of Xik Minru; to the east of Vissidal; lands at the south-west corner of 0xD6DD (0xD6DD0001 14, 14, z 56.5) among the Ridge Crown, Ashen Rise and Ember Terrace camps.

DELETE FROM `weenie` WHERE `class_Id` = 2000151;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000151, 'dw-fray-east-portal', 7, '2026-11-02 14:04:02') /* Portal */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000151,   1,      65536) /* ItemType - Portal */
     , (2000151,  16,         32) /* ItemUseable - Remote */
     , (2000151,  86,        300) /* MinLevel */
     , (2000151,  93,       3084) /* PhysicsState - Ethereal, ReportCollisions, Gravity, LightingOn */
     , (2000151, 111,          1) /* PortalBitmask - Unrestricted */
     , (2000151, 133,          4) /* ShowableOnRadar - ShowAlways */
     , (2000151, 9022,          1) /* PortalRealm */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000151,   1, True ) /* Stuck */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000151,  54,    -0.1) /* UseRadius */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000151,   1, 'Eastern Heights') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000151,   1, 0x020005D5) /* Setup */
     , (2000151,   2, 0x09000003) /* MotionTable */
     , (2000151,   8, 0x0600106B) /* Icon */;

INSERT INTO `weenie_properties_position` (`object_Id`, `position_Type`, `obj_Cell_Id`, `origin_X`, `origin_Y`, `origin_Z`, `angles_W`, `angles_X`, `angles_Y`, `angles_Z`)
VALUES (2000151, 2, 0xD6DD0001, 14, 14, 56.5, 1, 0, 0, 0) /* Destination */
/* @teleloc 0xD6DD0001 [14.000000 14.000000 56.500000] 1.000000 0.000000 0.000000 0.000000 */;
